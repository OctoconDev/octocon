defmodule Octocon.Primary.HordeSupervisor do
  use Horde.DynamicSupervisor

  require Logger

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

  # Only nodes in the primary region should be part of the supervisor
  defp members() do
    primary_region = Fly.RPC.primary_region()

    nodes =
      [Node.self() | Node.list()]
      |> Stream.filter(fn node ->
        case Fly.RPC.region(node) do
          {:ok, ^primary_region} -> true
          _ -> false
        end
      end)
      |> Enum.map(fn node -> {__MODULE__, node} end)

    Logger.info("Valid nodes (supervisor): #{inspect(nodes)}")

    nodes
  end
end
