defmodule Octocon.Application do
  @moduledoc """
  The OTP application for Octocon.

  This module is responsible for starting the Octocon application and its supervision tree.

  The application has different behaviors depending on the environment it is running in. Clustered nodes
  are split into one of three types:

  - `auxiliary` nodes, which are responsible for running an API endpoint (including distributed Phoenix channels)
    and connect to a read replica of the database.
  - `primary` nodes, which inherit all responsibilites of `auxiliary` nodes, but also run additional services
    such as Oban background jobs and Discord bot shards.
  - `sidecar` nodes, which act serve as dedicated, isolated environments for `primary` nodes to run CPU-intensive
    tasks such as image processing and encryption.

  Auxiliary nodes can be run anywhere in the world, while primary (and sidecar) nodes are only run in a single location
  in North America to have the lowest latency to Discord's servers (currently, Fly.io's `iad` region in Virginia).
  """

  use Application

  import Cachex.Spec

  require Logger

  @impl Application
  def start(_type, _args) do
    group = Octocon.RPC.NodeTracker.current_group()
    Logger.warning("Starting node of type: #{group}")

    children =
      global_children() ++
        group_children(group) ++
        [
          # Web endpoint
          OctoconWeb.Endpoint,
          {Bandit, plug: OctoconWeb.MetricsPlug, port: 9001}
        ]

    opts = [strategy: :one_for_one, name: Octocon.Supervisor]
    Supervisor.start_link(children, opts)
  end

  # Tell Phoenix to update the endpoint configuration
  # whenever the application is updated.
  @impl Application
  def config_change(changed, _new, removed) do
    OctoconWeb.Endpoint.config_change(changed, removed)
    :ok
  end

  defp global_children do
    [
      # Telemetry
      OctoconWeb.Telemetry,
      Octocon.PromEx,

      # Distribution
      generate_libcluster_child(),
      Octocon.RPC.NodeTracker,
      Supervisor.child_spec(
        {Cachex,
         name: Octocon.Cache.UserRegistry,
         hooks: [
           hook(
             module: Cachex.Limit.Scheduled,
             args: {20_000, [], [frequency: :timer.seconds(30)]}
           )
         ]},
        id: :user_registry_cache
      ),
      Octocon.Repo,

      # PubSub system
      {Phoenix.PubSub, name: Octocon.PubSub},

      # Finch (HTTP client)
      {Finch,
       name: Octocon.Finch,
       pools: %{
         :default => [size: 10],
         "https://cdn.discordapp.com" => [size: 32, count: 4]
       }}
    ]
    |> List.flatten()
  end

  defp group_children(:primary) do
    if Application.get_env(:octocon, :env) == :prod do
      [
        Octocon.FCM
      ]
    else
      []
    end ++
      [
        # TODO
        Octocon.MessageRepo,
        {Task, fn -> :mnesia.start() end},
        Octocon.Primary.Supervisor,
        Octocon.Global.Supervisor,
        OctoconDiscord.Supervisor
      ]
  end

  defp group_children(_), do: []

  defp generate_libcluster_child do
    node_list = Application.get_env(:octocon, :node_list, nil)
    use_tailscale = Application.get_env(:octocon, :use_tailscale, false)

    topologies =
      cond do
        use_tailscale ->
          [
            tailscale: [
              strategy: Cluster.Strategy.Tailscale,
              config: [
                tag: "beam",
                appname: "octo"
              ]
            ]
          ]

        node_list != nil && node_list != [] ->
          [
            static: [
              strategy: Cluster.Strategy.Epmd,
              config: [hosts: node_list]
            ]
          ]

        true ->
          nil
      end

    if topologies != nil do
      [
        {Cluster.Supervisor, [topologies, [name: Octocon.ClusterSupervisor]]}
      ]
    else
      Logger.warning("No clustering strategy configured, running as a single node!")

      Logger.warning(
        "Sidecar requests will also be executed locally. This may severely impact performance/latency."
      )

      []
    end
  end
end
