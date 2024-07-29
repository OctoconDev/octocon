defmodule Octocon.Primary.NodeListener do
  use GenServer

  alias Octocon.ClusterUtils

  require Logger

  def start_link(_), do: GenServer.start_link(__MODULE__, [])

  def init(_) do
    :net_kernel.monitor_nodes(true, node_type: :visible)
    {:ok, nil}
  end

  def handle_info({:nodeup, node, _node_type}, state) do
    Logger.info("Node up: #{node}")

    update_nodes()

    {:noreply, state}
  end

  def handle_info({:nodedown, node, _node_type}, state) do
    Logger.info("Node down: #{node}")

    update_nodes()

    {:noreply, state}
  end

  def update_nodes() do
    nodes_incl_self = ClusterUtils.primary_nodes(true)

    # nodes = Enum.filter(nodes_incl_self, fn node -> node != Node.self() end)

    set_horde_members(Octocon.Primary.HordeRegistry, nodes_incl_self)
    set_horde_members(Octocon.Primary.HordeSupervisor, nodes_incl_self)
  end

  defp set_horde_members(name, nodes) do
    members =
      nodes
      |> Enum.map(fn node -> {name, node} end)

    Logger.info("Valid nodes: #{inspect(members)}")

    :ok = Horde.Cluster.set_members(name, members)
  end
end
