defmodule Octocon.Global.Supervisor do
  use Supervisor

  require Logger

  def start_link(_), do: Supervisor.start_link(__MODULE__, [], name: __MODULE__)

  def init([]) do
    children = [
      Supervisor.child_spec({Task, fn -> init_global_front_notifier() end},
        id: :init_global_front_notifier
      ),
      Supervisor.child_spec({Task, fn -> init_global_link_token_registry() end},
        id: :init_global_link_token_registry
      )
    ]

    Supervisor.init(children, strategy: :one_for_one)
  end

  def init_global_front_notifier do
    Logger.info("Starting Global.FrontNotifier")

    Horde.DynamicSupervisor.start_child(
      Octocon.Primary.HordeSupervisor,
      Octocon.Global.FrontNotifier
    )
  end

  def init_global_link_token_registry do
    Logger.info("Starting Global.LinkTokenRegistry")

    Horde.DynamicSupervisor.start_child(
      Octocon.Primary.HordeSupervisor,
      Octocon.Global.LinkTokenRegistry
    )
  end
end
