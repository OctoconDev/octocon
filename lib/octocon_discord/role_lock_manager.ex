defmodule OctoconDiscord.RoleLockManager do
  @doc """
  Manages the role lock.
  """
  alias Octocon.RoleLocks
  alias Octocon.RoleLocks.RoleLock
  use GenServer
  require Logger

  @table :discord_role_locks

  # Client

  @doc false
  def start_link(_init_arg) do
    GenServer.start_link(__MODULE__, [], name: __MODULE__)
  end

  @doc """
  Sets a role as the role lock for a guild.
  If a lock already exists, it replaces the old one.
  """
  def set(guild_id, role_id) when is_binary(guild_id) and is_binary(role_id) do
    # We simply overwrite the ETS entry.
    # This satisfies "delete the old one and replace it with the new one" for the cache.
    :ets.insert(@table, {guild_id, role_id})

    # Assuming set_role_lock handles the DB transaction (upsert or delete+insert)
    # to enforce the unique constraint on guild_id.
    RoleLocks.set_role_lock(%{guild_id: guild_id, role_id: role_id})

    :ok
  end

  @doc """
  Unsets the role lock for a guild.
  """
  def remove(guild_id) when is_binary(guild_id) do
    case :ets.lookup(@table, guild_id) do
      [] ->
        {:error, :not_rolelocked}

      [{^guild_id, _role_id}] ->
        :ets.delete(@table, guild_id)
        RoleLocks.delete_role_lock(guild_id)
        :ok
    end
  end

  @doc """
  Gets the role lock ID for a guild, if one exists.
  Returns `nil` if no lock is set.
  """
  def get_lock(guild_id) when is_binary(guild_id) do
    case :ets.lookup(@table, guild_id) do
      [{^guild_id, role_id}] -> role_id
      [] -> nil
    end
  end

  # Server

  @doc false
  @impl true
  def init([]) do
    :ets.new(@table, [
      :set,
      :named_table,
      :public,
      read_concurrency: true,
      write_concurrency: true,
      decentralized_counters: true
    ])

    {:ok, [], {:continue, :load_role_locks}}
  end

  @impl true
  def handle_continue(:load_role_locks, state) do
    # TODO: Replace this with actual waiting if necessary
    Process.sleep(:timer.seconds(5))

    locks = RoleLocks.list_role_locks()

    # We map the DB results to {guild_id, role_id} tuples for ETS
    :ets.insert(
      @table,
      locks
      |> Enum.map(fn %{guild_id: gid, role_id: rid} -> {gid, rid} end)
    )

    {:noreply, state}
  end

  @impl true
  def handle_info(_, state) do
    {:noreply, state}
  end
end
