defmodule OctoconDiscord.ProxyCache do
  use GenServer
  require Logger

  alias Octocon.{
    Accounts,
    ClusterUtils
  }

  @table :discord_proxy_cache

  # Client

  def start_link(_init_arg) do
    GenServer.start_link(__MODULE__, [], name: __MODULE__)
  end

  def get(discord_id, require_proxies \\ true) when is_binary(discord_id) do
    unless Octocon.RPC.NodeTracker.primary?() do
      raise "ProxyCache should only be called on the primary region"
    end

    result = lookup_local(discord_id)

    cond do
      result == nil ->
        get_persisted_data(discord_id, require_proxies)

      result == :no_account ->
        Logger.debug("Memory cache: got gravestone for user #{discord_id}")
        {:error, :no_user}

      require_proxies && result.proxies == nil ->
        Logger.debug("Proxies evicted for #{discord_id}, revalidating")

        Process.send_after(__MODULE__, {:evict_proxies, discord_id}, :timer.minutes(5))

        data = %{result | proxies: build_existing_proxies(discord_id)}
        insert(discord_id, data)

        {:ok, data}

      true ->
        {:ok, result}
    end
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

  defp get_persisted_data(discord_id, include_proxies) do
    system_id = Accounts.id_from_system_identity({:discord, discord_id}, :system)

    if system_id == nil do
      Logger.debug("No system identity found for #{discord_id}, persisting gravestone")

      insert(discord_id, :no_account)

      {:error, :no_user}
    else
      if include_proxies do
        Process.send_after(__MODULE__, {:evict_proxies, discord_id}, :timer.minutes(5))
      end

      %{discord_settings: settings, primary_front: primary_front} =
        Octocon.Accounts.get_proxy_cache_data({:discord, discord_id})

      settings = settings || %Octocon.Accounts.DiscordSettings{}

      data = %{
        settings:
          settings
          |> Map.put(
            :server_settings,
            Octocon.Accounts.DiscordSettings.server_settings_map(settings)
          ),
        primary_front: primary_front,
        system_id: system_id,
        proxies: if(include_proxies, do: build_existing_proxies(discord_id), else: nil)
      }

      insert(discord_id, data)

      {:ok, data}
    end
  end

  def nuke_cache_internal do
    :ets.delete_all_objects(@table)
  end

  def nuke_cache do
    ClusterUtils.run_on_all_primary_nodes(fn ->
      OctoconDiscord.ProxyCache.nuke_cache_internal()
    end)
  end

  def invalidate_internal(discord_id) when is_binary(discord_id) do
    :ets.delete(@table, discord_id)
  end

  @doc """
  Invalidates the cache for a Discord user. Takes either a system identity or a Discord ID (binary).
  """
  def invalidate(system_identity)

  # User doesn't have a Discord ID
  def invalidate(nil), do: :ok

  def invalidate(system_identity) when is_tuple(system_identity) do
    discord_id = Accounts.id_from_system_identity(system_identity, :discord)

    if discord_id != nil do
      ClusterUtils.run_on_all_primary_nodes(fn ->
        OctoconDiscord.ProxyCache.invalidate_internal(discord_id)
      end)
    end

    :ok
  end

  def invalidate(discord_id) when is_binary(discord_id) do
    ClusterUtils.run_on_all_primary_nodes(fn ->
      OctoconDiscord.ProxyCache.invalidate_internal(discord_id)
    end)

    :ok
  end

  def update_primary_front(discord_id, alter_id), do: update(discord_id, :primary_front, alter_id)

  def insert(discord_id, data) do
    :ets.insert(@table, {discord_id, data})
  end

  def evict_proxies(discord_id) do
    update_internal(discord_id, :proxies, nil)
  end

  def update(nil, _, _), do: :ok

  def update(discord_id, key, value) do
    ClusterUtils.run_on_all_primary_nodes(fn ->
      OctoconDiscord.ProxyCache.update_internal(discord_id, key, value)
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
      |> Enum.sort(fn {{_, _, a}, _}, {{_, _, b}, _} -> a > b end)

    proxy_list
  end

  # Server

  @impl GenServer
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

  @impl GenServer
  def handle_info({:evict_proxies, discord_id}, state) do
    evict_proxies(discord_id)
    {:noreply, state}
  end

  @impl GenServer
  def handle_info(_, state) do
    {:noreply, state}
  end
end
