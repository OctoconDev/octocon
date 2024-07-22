defmodule OctoconDiscord.ChannelBlacklistManager do
  @doc """
  Manages the channel blacklist.

  TODO: Rework this to use the database instead of holding everything in ETS if memory pressure becomes an issue.
  """
  alias Octocon.ChannelBlacklists
  alias Octocon.ChannelBlacklists.ChannelBlacklist
  use GenServer
  require Logger

  @table :discord_channel_blacklists

  # Client

  @doc false
  def start_link(_init_arg) do
    GenServer.start_link(__MODULE__, [], name: __MODULE__)
  end

  @doc """
  Adds a channel to the blacklist.
  """
  def add(guild_id, channel_id) when is_binary(guild_id) and is_binary(channel_id) do
    if :ets.lookup(@table, channel_id) != [] do
      {:error, :already_blacklisted}
    else
      :ets.insert(@table, {channel_id, []})

      ChannelBlacklists.create_channel_blacklist(%{guild_id: guild_id, channel_id: channel_id})

      :ok
    end
  end

  @doc """
  Removes a channel from the blacklist.
  """
  def remove(channel_id) when is_binary(channel_id) do
    if :ets.lookup(@table, channel_id) == [] do
      {:error, :not_blacklisted}
    else
      :ets.delete(@table, channel_id)
      ChannelBlacklists.delete_channel_blacklist(%ChannelBlacklist{channel_id: channel_id})

      :ok
    end
  end

  @doc """
  Checks if a channel is blacklisted.
  """
  def is_blacklisted?(channel_id, parent_id)

  def is_blacklisted?(channel_id, nil) when is_binary(channel_id) do
    :ets.lookup(@table, channel_id) != []
  end

  def is_blacklisted?(channel_id, parent_id)
      when is_binary(channel_id) and is_binary(parent_id) do
    :ets.lookup(@table, channel_id) != [] or :ets.lookup(@table, parent_id) != []
  end

  @doc """
  Gets all blacklisted channels for a guild.
  """
  def get_all_for_guild(guild_id) when is_binary(guild_id) do
    ChannelBlacklists.list_channel_blacklists_by_guild(guild_id)
  end

  # Server

  @doc false
  @impl true
  def init([]) do
    channels = ChannelBlacklists.list_channel_blacklists_bare()

    :ets.new(@table, [
      :set,
      :named_table,
      :public,
      read_concurrency: true,
      write_concurrency: true,
      decentralized_counters: true
    ])

    :ets.insert(
      @table,
      channels
      |> Enum.map(fn channel_id -> {channel_id, []} end)
    )

    {:ok, []}
  end

  @impl true
  def handle_info(_, state) do
    {:noreply, state}
  end
end
