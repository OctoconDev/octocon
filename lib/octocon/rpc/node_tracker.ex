defmodule Octocon.RPC.NodeTracker do
  @moduledoc false

  use GenServer
  require Logger

  @table :octocon_nodes

  def start_link(opts) do
    GenServer.start_link(__MODULE__, opts, name: __MODULE__)
  end

  defp group_nodes(group) do
    case :ets.lookup(@table, group) do
      [{^group, nodes}] -> nodes
      [] -> []
    end
  end

  def primary_nodes, do: group_nodes(:primary)
  def auxiliary_nodes, do: group_nodes(:auxiliary)
  def sidecar_nodes, do: group_nodes(:sidecar)

  def sidecar_exists?, do: sidecar_nodes() != []

  def current_group do
    Application.get_env(:octocon, :node_group)
  end

  def primary?, do: current_group() == :primary

  @doc """
  Asks a node what group it is in.

  Returns `:error` if RPC is not supported on remote node.
  """
  def group(node) do
    rpc(node, {__MODULE__, :current_group, []})
  end

  def rpc_group(group, func, opts \\ [])

  def rpc_group(group, func, opts)
      when (is_function(func, 0) or is_tuple(func)) and
             is_list(opts) do
    if group == current_group() do
      invoke(func)
    else
      timeout = Keyword.get(opts, :timeout, 5_000)
      available_nodes = group_nodes(group)

      if Enum.empty?(available_nodes),
        do: raise(ArgumentError, "No node found running in group #{inspect(group)}")

      node = Enum.random(available_nodes)

      rpc(node, func, timeout)
    end
  end

  def rpc_group(region, {mod, func, args}, opts)
      when is_atom(mod) and is_list(args) and is_list(opts) do
    rpc_group(region, fn -> apply(mod, func, args) end, opts)
  end

  def rpc_primary(func, opts \\ [])

  def rpc_primary(func, opts) when is_function(func, 0) do
    rpc_group(:primary, func, opts)
  end

  def rpc_primary({module, func, args}, opts) do
    rpc_group(:primary, {module, func, args}, opts)
  end

  defp invoke(func) when is_function(func, 0), do: func.()
  defp invoke({mod, func, args}), do: apply(mod, func, args)

  @doc """
  Executes the function on the remote node and waits for the response.

  Exits after `timeout` milliseconds.
  """
  def rpc(node, func, timeout \\ 5000) do
    case erpc_call(node, func, timeout) do
      {:ok, result} ->
        result

      {:error, {:erpc, :timeout}} ->
        exit(:timeout)

      {:error, {:erpc, reason}} ->
        {:error, {:erpc, reason}}

      {:error, {:throw, value}} ->
        throw(value)

      {:error, {:exit, reason}} ->
        exit(reason)

      {:error, {_exception, reason, stack}} ->
        reraise(reason, stack)
    end
  end

  defp erpc_call(node, {mod, func, args}, timeout) do
    {:ok, :erpc.call(node, mod, func, args, timeout)}
  catch
    :throw, value -> {:error, {:throw, value}}
    :exit, reason -> {:error, {:exit, reason}}
    :error, {:erpc, reason} -> {:error, {:erpc, reason}}
    :error, {exception, reason, stack} -> {:error, {exception, reason, stack}}
  end

  defp erpc_call(node, func, timeout) when is_function(func, 0) do
    {:ok, :erpc.call(node, func, timeout)}
  catch
    :throw, value -> {:error, {:throw, value}}
    :exit, reason -> {:error, {:exit, reason}}
    :error, {:erpc, reason} -> {:error, {:erpc, reason}}
    :error, {exception, reason, stack} -> {:error, {exception, reason, stack}}
  end

  ## RPC calls run on local node

  def init(_opts) do
    table = :ets.new(@table, [:named_table, :public, read_concurrency: true])
    # Monitor new node up/down activity
    :global_group.monitor_nodes(true)
    {:ok, %{nodes: MapSet.new(), table: table}, {:continue, :get_node_groups}}
  end

  def handle_continue(:get_node_groups, state) do
    new_state =
      Enum.reduce(Node.list(:visible), state, fn node_name, acc ->
        put_node(acc, node_name)
      end)

    {:noreply, new_state}
  end

  def handle_info({:nodeup, node_name}, state) do
    Logger.debug("nodeup #{node_name}")

    # Only react/track visible nodes (hidden ones are for IEx, etc)
    if node_name in Node.list(:visible) do
      {:noreply, put_node(state, node_name)}
    else
      {:noreply, state}
    end
  end

  def handle_info({:nodedown, node_name}, state) do
    Logger.debug("nodedown #{node_name}")
    {:noreply, drop_node(state, node_name)}
  end

  @doc false
  def put_node(state, node_name) do
    group = group(node_name)

    Logger.info("Discovered node #{inspect(node_name)} in group #{group}")
    group_nodes = group_nodes(group)
    :ets.insert(state.table, {group, [node_name | group_nodes]})

    %{state | nodes: MapSet.put(state.nodes, {node_name, group})}
  end

  @doc false
  def drop_node(state, node_name) do
    # Find the node information for the node going down.
    case get_node(state, node_name) do
      {^node_name, group} ->
        Logger.info("Dropping node #{inspect(node_name)} in group #{group}")
        group_nodes = group_nodes(group)
        # Remove the node from the known regions and update the local cache
        new_groups = Enum.reject(group_nodes, fn n -> n == node_name end)
        :ets.insert(state.table, {group, new_groups})

        # Remove the node entry from the GenServer's state
        new_nodes =
          Enum.reduce(state.nodes, state.nodes, fn
            {^node_name, ^group}, acc -> MapSet.delete(acc, {node_name, group})
            {_node, _group}, acc -> acc
          end)

        # Return the new state
        %{state | nodes: new_nodes}

      # Node is not known to us. Ignore it.
      nil ->
        state
    end
  end

  defp get_node(state, name) do
    Enum.find(state.nodes, fn {n, _group} -> n == name end)
  end
end
