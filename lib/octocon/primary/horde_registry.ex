defmodule Octocon.Primary.HordeRegistry do
  use Horde.Registry

  require Logger

  alias Octocon.ClusterUtils

  def start_link(_) do
    Horde.Registry.start_link(__MODULE__, [keys: :unique], name: __MODULE__)
  end

  def init(init_arg) do
    [members: members()]
    |> Keyword.merge(init_arg)
    |> Horde.Registry.init()
  end

  # Only nodes marked as primary should be part of the registry
  defp members do
    nodes =
      ClusterUtils.primary_nodes(true)
      |> Enum.map(fn node -> {__MODULE__, node} end)

    Logger.info("Valid nodes (supervisor): #{inspect(nodes)}")

    nodes
  end
end
