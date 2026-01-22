defmodule OctoconDiscord.Commands.Help do
  @moduledoc false

  @behaviour Nosedrum.ApplicationCommand

  alias OctoconDiscord.Components.HelpHandler

  @impl Nosedrum.ApplicationCommand
  def description, do: "Displays an interactive guide on how to use the Octocon bot."

  @impl Nosedrum.ApplicationCommand
  def command(_interaction) do
    HelpHandler.handle_init()
  end

  @impl Nosedrum.ApplicationCommand
  def type, do: :slash

  # @impl Nosedrum.ApplicationCommand
  # def options, do: []
end
