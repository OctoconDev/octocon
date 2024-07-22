defmodule OctoconDiscord.Supervisor do
  @moduledoc """
  Root supervisor for the Discord portion of the Octocon application. This supervisor is guaranteed to be running on a primary node.
  """
  use Supervisor

  import Cachex.Spec

  alias Octocon.ClusterUtils

  require Logger

  def start_link(_init_arg) do
    Supervisor.start_link(__MODULE__, [], name: __MODULE__)
  end

  @impl true
  def init([]) do
    children = [
      Nostrum.Application,
      # Server settings cache (each guild cached for 10 minutes)
      Supervisor.child_spec(
        {Cachex,
         name: OctoconDiscord.Cache.ServerSettings,
         limit: limit(size: 5000),
         fallback: fallback(default: &OctoconDiscord.ServerSettingsManager.cache_function/1),
         expiration: expiration(default: :timer.minutes(10))},
        id: :server_settings_cache
      ),
      # Webhooks cache (each channel cached for 10 minutes)
      Supervisor.child_spec(
        {Cachex,
         name: OctoconDiscord.Cache.Webhooks,
         limit: limit(size: 5000),
         fallback: fallback(default: &OctoconDiscord.WebhookManager.cache_function/1),
         expiration: expiration(default: :timer.minutes(10))},
        id: :webhooks_cache
      ),
      # User last message cache (each user's last message cached for 2 minutes)
      Supervisor.child_spec(
        {Cachex,
         name: OctoconDiscord.Cache.LastMessage,
         expiration: expiration(default: :timer.minutes(2))},
        id: :last_message_cache
      ),
      # Custom ETS-backed persistent caches
      OctoconDiscord.ProxyCache,
      OctoconDiscord.ChannelBlacklistManager,
      # Gateway events
      Supervisor.child_spec({Task, fn -> start_unique_consumer() end}, id: :start_unique_consumer),
      # Component handlers
      OctoconDiscord.Components.HelpHandler,
      OctoconDiscord.Components.AlterPaginator,
      OctoconDiscord.Components.WipeAltersHandler,
      OctoconDiscord.Components.DeleteAccountHandler,
      # Application commands
      {Nosedrum.Storage.Dispatcher, name: Nosedrum.Storage.Dispatcher},
      Supervisor.child_spec({Task, fn -> init_shards() end}, id: :init_shards)
    ]

    Supervisor.init(children, strategy: :one_for_one)
  end

  defp init_shards() do
    node_count = ClusterUtils.primary_node_count()
    desired_shards = OctoconDiscord.get_desired_shards()

    for i <- 1..node_count do
      # If desired_shards is 100, and we have 4 nodes, we want to start shards 0-24 on node 1, 25-49 on node 2, etc.
      start_shard = div((i - 1) * desired_shards, node_count)
      end_shard = div(i * desired_shards, node_count) - 1

      Horde.DynamicSupervisor.start_child(
        Octocon.Primary.HordeSupervisor,
        {OctoconDiscord.ShardManager, {i, start_shard, end_shard, desired_shards}}
      )
    end
  end

  def start_unique_consumer() do
    Logger.info("Starting unique consumer")

    Horde.DynamicSupervisor.start_child(
      Octocon.Primary.HordeSupervisor,
      OctoconDiscord.ConsumerSupervisor
    )
  end
end
