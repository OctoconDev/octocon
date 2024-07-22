defmodule OctoconDiscord.ConsumerSupervisor do
  use Supervisor

  require Logger

  @via {:via, Horde.Registry, {Octocon.Primary.HordeRegistry, __MODULE__}}

  def start_link(_init_arg) do
    case Supervisor.start_link(__MODULE__, [], name: @via) do
      {:ok, pid} ->
        {:ok, pid}

      {:error, {:already_started, pid}} ->
        Logger.warning(
          "OctoconDiscord.ConsumerSupervisor already started at #{inspect(pid)}, returning :ignore"
        )

        :ignore
    end
  end

  def init([]) do
    children = [
      OctoconDiscord.Consumer
    ]

    Supervisor.init(children, strategy: :one_for_one)
  end

  def get_consumer_pid() do
    case Horde.Registry.lookup(Octocon.Primary.HordeRegistry, __MODULE__) do
      [] ->
        :error

      [{pid, _}] ->
        [{_, consumer_pid, _, _}] = Supervisor.which_children(pid)
        {:ok, consumer_pid}
    end
  end
end
