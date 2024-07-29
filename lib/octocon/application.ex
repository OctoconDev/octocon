defmodule Octocon.Application do
  @moduledoc """
  The OTP application for Octocon.

  This module is responsible for starting the Octocon application and its supervision tree.

  The application has different behaviors depending on the environment it is running in. Clustered nodes
  are split into one of two types:

  - `auxiliary` nodes, which are responsible for running an API endpoint (including distributed Phoenix channels)
    and connect to a read replica of the database.
  - `primary` nodes, which inherit all responsibilites of `auxiliary` nodes, but also run additional services
    such as Oban background jobs and Discord bot shards.

  Auxiliary nodes automatically proxy any non-read database queries and Oban jobs to the primary node through `Fly.Postgres`
  and `Fly.RPC`. Reads are done locally, and are therefore much faster.

  Auxiliary nodes can be run anywhere in the world, while primary nodes are only run in a single location in North America
  to have the lowest latency to Discord's servers (currently, Fly.io's `iad` region in Virginia).
  """

  use Application

  require Logger

  @impl true
  def start(_type, _args) do
    children =
      global_children_start() ++ primary_children() ++ global_children_end()

    opts = [strategy: :one_for_one, name: Octocon.Supervisor]
    Supervisor.start_link(children, opts)
  end

  # Tell Phoenix to update the endpoint configuration
  # whenever the application is updated.
  @impl true
  def config_change(changed, _new, removed) do
    OctoconWeb.Endpoint.config_change(changed, removed)
    :ok
  end

  defp global_children_start() do
    [
      # Telemetry
      OctoconWeb.Telemetry,
      Octocon.PromEx,
      # Distribution
      # {Task, fn -> Node.connect(:"node1@127.0.0.1") end},
      {Octocon.DNSCluster, query: Application.get_env(:octocon, :dns_cluster_query) || :ignore},
      # Distribution
      {Fly.RPC, []},
      # Ecto (Postgres database) repositories
      Octocon.Repo.Local,
      {Fly.Postgres.LSN.Supervisor, repo: Octocon.Repo.Local},
      # Background jobs
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
  end

  defp global_children_end() do
    [
      # Web endpoint
      OctoconWeb.Endpoint,
      {Bandit, plug: OctoconWeb.MetricsPlug, port: 9001}
    ]
  end

  defp prod_primary_children() do
    if Application.get_env(:octocon, :env) == :prod do
      [
        Octocon.FCM
      ]
    else
      []
    end
  end

  defp primary_children do
    if Fly.RPC.is_primary?() do
      if Application.get_env(:octocon, :env) == :prod do
        prod_primary_children()
      else
        []
      end ++
        [
          {Task, fn -> :mnesia.start() end},
          Octocon.Primary.Supervisor,
          Octocon.Global.Supervisor,
          {Oban, Application.fetch_env!(:octocon, Oban)},
          # Discord
          OctoconDiscord.Supervisor
        ]
    else
      []
    end
  end
end
