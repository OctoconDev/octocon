defmodule OctoconDiscord.StatusUpdater do
  @moduledoc """
  Manages a subset of shards for the Octocon Discord bot.
  """

  use GenServer

  require Logger

  @via {:via, Horde.Registry, {Octocon.Primary.HordeRegistry, __MODULE__}}

  @interval :timer.minutes(5)

  def start_link([]) do
    case GenServer.start_link(__MODULE__, [], name: @via) do
      {:ok, pid} ->
        {:ok, pid}

      {:error, {:already_started, pid}} ->
        Logger.info(
          "OctoconDiscord.StatusUpdater already started at #{inspect(pid)}, returning :ignore"
        )

        :ignore
    end
  end

  def start do
    GenServer.call(@via, :start)
  end

  @impl GenServer
  def init([]) do
    {:ok, :stopped}
  end

  @impl GenServer
  def handle_call(:start, _, :started) do
    {:reply, :already_started, :started}
  end

  @impl GenServer
  def handle_call(:start, _, :stopped) do
    Logger.info("Starting OctoconDiscord.StatusUpdater loop")

    Process.send_after(self(), :update, :timer.seconds(30))

    {:reply, :ok, :started}
  end

  @impl GenServer
  def handle_info(:update, state) do
    update_status()
    Process.send_after(self(), :update, @interval)

    {:noreply, state}
  end

  defp update_status do
    Logger.info("Updating Discord status...")

    guild_count = :mnesia.table_info(Nostrum.Cache.GuildCache.Mnesia.table(), :size)

    Nostrum.Api.Self.update_status(
      :online,
      "/help | In #{format_number(guild_count)} servers!",
      0
    )
  end

  # E.g. 12345678 to "12,345,678"
  def format_number(num) do
    num
    |> :erlang.integer_to_list()
    |> Enum.reverse()
    |> Enum.chunk_every(3)
    |> Enum.map(&Enum.reverse/1)
    |> Enum.reverse()
    |> Enum.join(",")
  end
end
