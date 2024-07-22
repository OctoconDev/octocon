defmodule OctoconDiscord.ProxyCache do
  use GenServer
  require Logger

  alias Octocon.Accounts
  alias Octocon.ClusterUtils
  alias Octocon.ProxyCache, as: Persistence

  @table :discord_proxy_cache

  # Client

  def start_link(_init_arg) do
    GenServer.start_link(__MODULE__, [], name: __MODULE__)
  end

  defp lookup_local(discord_id) when is_binary(discord_id) do
    case :ets.lookup(@table, discord_id) do
      [] ->
        Logger.debug("Memory cache miss for #{discord_id}")
        nil

      [{_, data}] ->
        Logger.debug("Memory cache hit for #{discord_id}")
        data
    end
  end

  def get(discord_id, include_proxies \\ true) when is_binary(discord_id) do
    unless Fly.RPC.is_primary?() do
      raise "ProxyCache should only be called on the primary region"
    end

    result = lookup_local(discord_id)

    cond do
      result == nil ->
        get_persisted_data(discord_id, include_proxies)

      result == :no_account ->
        Logger.debug("Memory cache: got gravestone for user #{discord_id}")
        {:error, :no_user}

      include_proxies && result.proxies == nil ->
        Logger.debug("Proxies evicted for #{discord_id}, revalidating")

        Process.send_after(__MODULE__, {:evict_proxies, discord_id}, :timer.minutes(5))

        data = %{result | proxies: build_existing_proxies(discord_id)}

        insert(discord_id, data, false)

        {:ok, data}

      true ->
        {:ok, result}
    end
  end

  defp get_persisted_data(discord_id, include_proxies) do
    case Persistence.get_proxy_cache_item(discord_id) do
      nil ->
        system_id = Accounts.id_from_system_identity({:discord, discord_id}, :system)

        case system_id do
          nil ->
            Logger.debug("No system identity found for #{discord_id}, persisting gravestone")

            insert(discord_id, :no_account)

            {:error, :no_user}

          system_id ->
            Logger.debug("Persisted cache miss for #{discord_id}; creating new entry")

            if include_proxies do
              Process.send_after(__MODULE__, {:evict_proxies, discord_id}, :timer.minutes(5))
            end

            %{
              primary_front: primary_front,
              system_tag: system_tag,
              show_system_tag: show_system_tag,
              case_insensitive_proxying: case_insensitive_proxying,
              show_proxy_pronouns: show_proxy_pronouns,
              ids_as_proxies: ids_as_proxies
            } = Octocon.Accounts.get_proxy_data_bulk({:discord, discord_id})

            data = %{
              mode: :none,
              system_id: system_id,
              primary_front: primary_front,
              system_tag: system_tag,
              show_system_tag: show_system_tag,
              case_insensitive_proxying: case_insensitive_proxying,
              show_proxy_pronouns: show_proxy_pronouns,
              ids_as_proxies: ids_as_proxies,
              proxies: if(include_proxies, do: build_existing_proxies(discord_id), else: nil)
            }

            insert(discord_id, data)

            {:ok, data}
        end

      data ->
        Logger.debug("Persisted cache hit for #{discord_id}")

        decoded_data =
          :erlang.binary_to_term(data)

        case decoded_data do
          :n ->
            Logger.debug("Persisted cache: hit a gravestone for #{discord_id}")
            insert(discord_id, :no_account, false)

            {:error, :no_user}

          _ ->
            data =
              decoded_data
              |> Map.put(
                :proxies,
                if(include_proxies, do: build_existing_proxies(discord_id), else: nil)
              )

            insert(discord_id, data, false)

            {:ok, data}
        end
    end
  end

  def invalidate_internal(discord_id) when is_binary(discord_id) do
    :ets.delete(@table, discord_id)
  end

  @doc """
  Invalidates the cache for a Discord user. Takes either a system identity or a Discord ID (binary).
  """
  def invalidate(system_identity) when is_tuple(system_identity) do
    discord_id = Accounts.id_from_system_identity(system_identity, :discord)

    Persistence.delete_proxy_cache_item(discord_id)

    ClusterUtils.run_on_all_primary_nodes(fn ->
      OctoconDiscord.ProxyCache.invalidate_internal(discord_id)
    end)

    :ok
  end

  def invalidate(discord_id) when is_binary(discord_id) do
    Persistence.delete_proxy_cache_item(discord_id)

    ClusterUtils.run_on_all_primary_nodes(fn ->
      OctoconDiscord.ProxyCache.invalidate_internal(discord_id)
    end)

    :ok
  end

  def update_primary_front(discord_id, alter_id), do: update(discord_id, :primary_front, alter_id)

  def insert(discord_id, data, persist \\ true) do
    res = :ets.insert(@table, {discord_id, data})

    if persist do
      spawn(fn ->
        Persistence.upsert_proxy_cache_item(
          discord_id,
          case data do
            :no_account -> :n
            data -> Map.put(data, :proxies, nil)
          end
        )
      end)
    end

    res
  end

  def update_system_tag(discord_id, system_tag),
    do: update(discord_id, :system_tag, system_tag)

  def update_show_system_tag(discord_id, show_system_tag),
    do: update(discord_id, :show_system_tag, show_system_tag)

  def update_case_insensitive_proxying(discord_id, case_insensitive_proxying),
    do: update(discord_id, :case_insensitive_proxying, case_insensitive_proxying)

  def update_show_proxy_pronouns(discord_id, show_proxy_pronouns),
    do: update(discord_id, :show_proxy_pronouns, show_proxy_pronouns)

  def update_mode(discord_id, mode), do: update(discord_id, :mode, mode)

  def update_ids_as_proxies(discord_id, ids_as_proxies),
    do: update(discord_id, :ids_as_proxies, ids_as_proxies)

  def evict_proxies(discord_id) do
    update_internal(discord_id, :proxies, nil)
  end

  def update(nil, _, _), do: :ok

  def update(discord_id, key, value) do
    data =
      case Persistence.get_proxy_cache_item(discord_id) do
        nil ->
          # Not in the database, create a new entry
          persist_new_cache_data(discord_id, key, value)

        data ->
          # Already in the database, update the entry
          decoded_data =
            :erlang.binary_to_term(data)
            |> Map.put(key, value)

          Persistence.upsert_proxy_cache_item(discord_id, decoded_data)

          decoded_data
      end

    # Now we propagate the change to all primary nodes
    ClusterUtils.run_on_all_primary_nodes(fn ->
      OctoconDiscord.ProxyCache.update_internal(discord_id, data)
    end)

    :ok
  end

  def update_internal(discord_id, key, value) do
    result = lookup_local(to_string(discord_id))

    if result != nil do
      Logger.debug("Updating cached data for #{discord_id}")

      insert(discord_id, Map.put(result, key, value))
    end

    :ok
  end

  def update_internal(discord_id, data) do
    result = lookup_local(to_string(discord_id))

    if result != nil do
      Logger.debug("Updating cached data for #{discord_id}")

      insert(discord_id, data)
    end

    :ok
  end

  defp persist_new_cache_data(discord_id, key, value) do
    system_id = Accounts.id_from_system_identity({:discord, discord_id}, :system)

    %{
      primary_front: primary_front,
      system_tag: system_tag,
      show_system_tag: show_system_tag,
      case_insensitive_proxying: case_insensitive_proxying,
      show_proxy_pronouns: show_proxy_pronouns,
      ids_as_proxies: ids_as_proxies
    } = Octocon.Accounts.get_proxy_data_bulk({:system, system_id})

    data =
      %{
        mode: :none,
        system_id: system_id,
        primary_front: primary_front,
        system_tag: system_tag,
        show_system_tag: show_system_tag,
        case_insensitive_proxying: case_insensitive_proxying,
        show_proxy_pronouns: show_proxy_pronouns,
        ids_as_proxies: ids_as_proxies,
        proxies: nil
      }
      |> Map.put(key, value)

    Persistence.upsert_proxy_cache_item(discord_id, data)

    data
  end

  def build_existing_proxies(discord_id) do
    proxy_map = Octocon.Accounts.get_user_proxy_map_old({:discord, discord_id})

    proxy_list =
      proxy_map
      |> Enum.reduce([], fn {proxy, {system_id, alter_id}}, acc ->
        [prefix, suffix] =
          proxy
          |> String.trim()
          |> String.split("text", parts: 2)

        [
          {{prefix, suffix, String.length(proxy)}, {system_id, alter_id}}
          | acc
        ]
      end)
      |> Enum.sort(fn {{_, _, a}, _}, {{_, _, b}, _} -> a < b end)

    proxy_list

    # proxy_list =
    #   proxies
    #   |> Enum.map(fn proxy ->
    #     [prefix, suffix] =
    #       proxy
    #       |> String.trim()
    #       |> String.split("text", parts: 2)

    #     {{prefix, suffix, String.length(proxy)}, {system_id, alter_id}}
    #   end)
    #   |> Enum.sort(fn {{_, _, a}, _}, {{_, _, b}, _} -> a < b end)
  end

  # def build_existing_proxies_new(discord_id) do
  #   %{
  #     user_id: user_id,
  #     proxies: proxies
  #   } = Octocon.Accounts.get_user_proxy_map({:discord, discord_id})
  # end

  # Server

  @impl true
  def init([]) do
    :ets.new(@table, [
      :set,
      :named_table,
      :public,
      read_concurrency: true,
      write_concurrency: :auto,
      decentralized_counters: true
    ])

    {:ok, []}
  end

  # @impl true
  # def handle_continue(:init_dump, state) do
  #   send(__MODULE__, :dump)
  #   {:noreply, state}
  # end

  @impl true
  def handle_info({:evict_proxies, discord_id}, state) do
    evict_proxies(discord_id)
    {:noreply, state}
  end

  # @impl true
  # def handle_info(:dump, state) do
  #   Logger.debug("Dumping proxy cache")

  #   :ets.tab2list(@table)
  #   # Remove proxies
  #   |> Enum.map(fn {discord_id, data} -> {discord_id, %{data | proxies: nil}} end)
  #   |> :erlang.term_to_binary()
  #   |> Persistence.set_pc_items()

  #   Process.send_after(__MODULE__, :dump, :timer.minutes(5))
  #   {:noreply, state}
  # end

  @impl true
  def handle_info(_, state) do
    {:noreply, state}
  end
end
