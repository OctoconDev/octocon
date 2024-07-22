# defmodule OctoconDiscord.Commands.Octo do
#   @moduledoc false

#   @behaviour Nosedrum.ApplicationCommand

#   alias OctoconDiscord.Components.HelpHandler

#   @impl true
#   def description, do: "Displays an all-in-one interface to interact with Octocon."

#   @impl true
#   def command(_interaction) do
#     HelpHandler.handle_init()
#   end

#   @impl true
#   def type, do: :slash

#   # @impl true
#   # def options, do: []
# end
