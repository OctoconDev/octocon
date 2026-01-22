defmodule Cluster.Strategy.Tailscale do
  @moduledoc """
  Cluster strategy for connecting Elixir nodes over Tailscale. Modified to consider devices by a given tag instead of their hostname.

      config :libcluster,
        topologies: [
          tailscale: [
            strategy: #{__MODULE__},
            config: [
              authkey: "",
              tailnet: "",
              tag: "",
              appname: ""
            ]
          ]
        ]

  """
  use GenServer
  alias Cluster.Strategy.State

  @polling_interval 30_000

  def start_link(args), do: GenServer.start_link(__MODULE__, args)

  @impl GenServer
  def init([%State{meta: nil} = state]) do
    init([%State{state | :meta => MapSet.new()}])
  end

  def init([%State{} = state]) do
    {:ok, load(state)}
  end

  @impl GenServer
  def handle_info(:timeout, state) do
    handle_info(:load, state)
  end

  def handle_info(:load, %State{} = state) do
    {:noreply, load(state)}
  end

  def handle_info(_, state) do
    {:noreply, state}
  end

  defp load(%State{} = state) do
    nodes =
      state
      |> get_nodes()
      |> disconnect_nodes(state)
      |> connect_nodes(state)

    Process.send_after(self(), :load, @polling_interval)
    %{state | meta: nodes}
  end

  defp get_nodes(%State{config: config}) do
    tag = Keyword.fetch!(config, :tag)
    appname = Keyword.fetch!(config, :appname)

    Octocon.Utils.Tailscale.list_devices()
    |> Enum.filter(fn device ->
      ("tag:" <> tag) in (device["tags"] || []) && device["connectedToControl"] == true
    end)
    |> Enum.map(&List.first(&1["addresses"]))
    |> Enum.map(&"#{appname}@#{&1}")
    |> Enum.map(&String.to_atom/1)
    |> MapSet.new()
  end

  defp disconnect_nodes(nodes, %State{} = state) do
    removed = MapSet.difference(state.meta, nodes)

    case Cluster.Strategy.disconnect_nodes(
           state.topology,
           state.disconnect,
           state.list_nodes,
           MapSet.to_list(removed)
         ) do
      :ok ->
        nodes

      {:error, bad_nodes} ->
        # Add back the nodes we couldn't remove
        Enum.reduce(bad_nodes, nodes, fn {n, _}, acc ->
          MapSet.put(acc, n)
        end)
    end
  end

  defp connect_nodes(nodes, %State{} = state) do
    case Cluster.Strategy.connect_nodes(
           state.topology,
           state.connect,
           state.list_nodes,
           MapSet.to_list(nodes)
         ) do
      :ok ->
        nodes

      {:error, bad_nodes} ->
        # Remove the nodes we couldn't add
        Enum.reduce(bad_nodes, nodes, fn {n, _}, acc ->
          MapSet.delete(acc, n)
        end)
    end
  end
end
