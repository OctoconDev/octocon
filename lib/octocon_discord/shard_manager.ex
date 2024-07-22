defmodule OctoconDiscord.ShardManager do
  @moduledoc """
  Manages a subset of shards for the Octocon Discord bot.
  """

  use GenServer

  require Logger

  def child_spec({id, _, _, _} = init_arg) do
    %{
      id: "#{__MODULE__}-#{id}",
      start: {__MODULE__, :start_link, [init_arg]},
      shutdown: 10_000
    }
  end

  def start_link({id, _, _, _} = init_arg) do
    case GenServer.start_link(__MODULE__, init_arg, name: via_tuple(id)) do
      {:ok, pid} ->
        {:ok, pid}

      {:error, {:already_started, pid}} ->
        Logger.info(
          "ShardManager with ID #{id} already started at #{inspect(pid)}, returning :ignore"
        )

        :ignore
    end
  end

  @impl true
  def init(init_arg) do
    Logger.info("Starting ShardManager with ID #{inspect(init_arg)}")
    Process.flag(:trap_exit, true)
    {:ok, init_arg, {:continue, :connect_shards}}
  end

  @impl true
  def handle_continue(:connect_shards, state) do
    connect_shards(state)

    {:noreply, state}
  end

  @impl true
  def terminate(_reason, {id, _, _, _} = state) do
    # TODO: Shard handoff à la https://github.com/ElixirSeattle/tanx/tree/master/apps/tanx/lib/tanx/game/manager.ex (https://www.youtube.com/watch?v=nLApFANtkHs)
    Logger.info("Terminating ShardManager with ID #{id}")
    disconnect_shards(state)

    :ok
  end

  @impl true
  def handle_info({:EXIT, _pid, reason}, state) do
    {:stop, reason, state}
  end

  defp connect_shards({id, start_shard, end_shard, desired_shards}) do
    Logger.info(
      "Connecting shards for ID #{id}; desired shards #{desired_shards}; start shard #{start_shard}; end shard #{end_shard}"
    )

    for shard <- start_shard..end_shard do
      Nostrum.Shard.Supervisor.connect(shard, desired_shards)
    end
  end

  defp disconnect_shards({id, start_shard, end_shard, desired_shards}) do
    Logger.info(
      "Disconnecting shards for ID #{id}; desired shards #{desired_shards}; start shard #{start_shard}; end shard #{end_shard}"
    )

    for shard <- start_shard..end_shard do
      Logger.debug("Disconnecting shard #{shard}")
      data = Nostrum.Shard.Supervisor.disconnect(shard)
      Logger.debug("Disconnected shard #{shard}: #{inspect(data)}")
      data
    end
  end

  def via_tuple(id) do
    {:via, Horde.Registry, {Octocon.Primary.HordeRegistry, "#{__MODULE__}-#{id}"}}
  end
end
