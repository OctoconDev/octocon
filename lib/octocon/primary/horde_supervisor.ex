defmodule Octocon.Primary.HordeSupervisor do
  use Horde.DynamicSupervisor

  require Logger

  alias Octocon.ClusterUtils

  def start_link(_) do
    Horde.DynamicSupervisor.start_link(
      __MODULE__,
      [strategy: :one_for_one],
      name: __MODULE__
    )
  end

  def init(init_arg) do
    [members: members()]
    |> Keyword.merge(init_arg)
    |> Horde.DynamicSupervisor.init()
  end

  # Only nodes in the primary group should be part of the supervisor
  defp members do
    nodes =
      ClusterUtils.primary_nodes(true)
      |> Enum.map(fn node -> {__MODULE__, node} end)

    Logger.info("Valid nodes (supervisor): #{inspect(nodes)}")

    nodes
  end
end
