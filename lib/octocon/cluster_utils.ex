defmodule Octocon.ClusterUtils do
  @moduledoc """
  Utility functions for working with the currently running node cluster.

  Currently backed by Fly.RPC.
  """

  alias Fly.RPC

  @doc """
  Get the primary region of the cluster.
  """
  def primary_region, do: RPC.primary_region()

  @doc """
  Check if the current node is a primary node.
  """
  def is_primary?, do: RPC.is_primary?()

  @doc """
  Get a list of all primary nodes in the cluster.

  If `include_self` is `true`, the current node will be included in the list if it is a primary node. Otherwise, the current node will be excluded.
  """
  def primary_nodes(include_self \\ false)

  def primary_nodes(false) do
    primary_region()
    |> RPC.region_nodes()
  end

  def primary_nodes(true) do
    other_nodes = primary_nodes(false)

    if is_primary?() do
      [Node.self() | other_nodes]
    else
      other_nodes
    end
  end

  @doc """
  Run the given function on all primary nodes in the cluster. If the current node is a primary node, the function will be run locally as well
  without any RPC overhead.
  """
  def run_on_all_primary_nodes(fun) do
    # If we are a primary node, run the function locally as well
    if RPC.is_primary?() do
      fun.()
    end

    primary_nodes()
    |> Task.async_stream(fn node ->
      RPC.rpc(node, fun)
    end)
    |> Enum.map(fn
      {:ok, result} -> {:ok, result}
      {:error, reason} -> {:error, reason}
    end)
  end

  @doc """
  Get the number of desired functional (non-standby) primary nodes in the cluster.
  """
  def primary_node_count do
    Application.get_env(:octocon, :primary_node_count, 1)
  end
end
