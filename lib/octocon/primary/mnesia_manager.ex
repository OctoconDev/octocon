defmodule Octocon.Primary.MnesiaManager do
  # TODO: Docs
  @moduledoc false

  use GenServer

  require Logger

  def start_link(_), do: GenServer.start_link(__MODULE__, [], name: __MODULE__)

  def init(_) do
    Logger.info("Starting Mnesia")

    :stopped = :mnesia.stop()

    :ok = :mnesia.start()

    # ensure_disc_copies(Node.self())

    {:ok, []}
  end

  def update_nodes(nodes) do
    GenServer.call(__MODULE__, {:update_nodes, nodes})
  end

  def handle_call({:update_nodes, nodes}, _from, state) do
    Logger.info("Updating Mnesia nodes: #{inspect(nodes)}")

    case :mnesia.change_config(:extra_db_nodes, nodes) do
      {:ok, _} ->
        Logger.info("Mnesia nodes updated")

      {:error, reason} ->
        Logger.error("Failed to update Mnesia nodes: #{inspect(reason)}")
    end

    Logger.info("Mnesia nodes updated")

    {:reply, :ok, state}
  end

  # defp ensure_disc_copies(node) do
  #   for table <- :mnesia.system_info(:tables) do
  #     disc_copies = :mnesia.table_info(table, :disc_copies)

  #     unless node in disc_copies do
  #       Logger.info("Adding disc copy of #{table} to #{node}")
  #       case :mnesia.add_table_copy(table, node, :disc_copies) do
  #         {:atomic, :ok} -> Logger.info("Disc copy of #{table} added to #{node}")
  #         {:aborted, reason} -> Logger.error("Failed to add disc copy of #{table} to #{node}: #{inspect(reason)}")
  #       end
  #     end
  #   end
  # end

  # defp update_disc_copies(nodes) do
  #   for table <- :mnesia.system_info(:tables) do
  #     current_disc_copies = :mnesia.table_info(table, :disc_copies)

  #     # Add disc copies to new nodes if not already present
  #     for node <- nodes do
  #       unless node in current_disc_copies do
  #         Logger.info("Adding disc copy of #{table} to #{node}")
  #         case :mnesia.add_table_copy(table, node, :disc_copies) do
  #           {:atomic, :ok} -> Logger.info("Disc copy of #{table} added to #{node}")
  #           {:aborted, reason} -> Logger.error("Failed to add disc copy of #{table} to #{node}: #{inspect(reason)}")
  #         end
  #       end
  #     end
  #   end
  # end
end
