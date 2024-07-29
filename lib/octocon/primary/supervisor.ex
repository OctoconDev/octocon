defmodule Octocon.Primary.Supervisor do
  use Supervisor

  def start_link(_), do: Supervisor.start_link(__MODULE__, [], name: __MODULE__)

  def init([]) do
    children = [
      Octocon.MessageRepo,
      Octocon.Primary.NodeListener,
      Octocon.Primary.HordeRegistry,
      Octocon.Primary.HordeSupervisor
    ]

    Supervisor.init(children, strategy: :one_for_one)
  end
end
