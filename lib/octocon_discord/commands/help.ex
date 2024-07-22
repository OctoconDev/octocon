defmodule OctoconDiscord.Commands.Help do
  @moduledoc false

  @behaviour Nosedrum.ApplicationCommand

  alias OctoconDiscord.Components.HelpHandler

  @impl true
  def description, do: "Displays an interactive guide on how to use the Octocon bot."

  @impl true
  def command(_interaction) do
    HelpHandler.handle_init()
  end

  @impl true
  def type, do: :slash

  # @impl true
  # def options, do: []
end
