defmodule OctoconDiscord.Components.HelpHandler.Pages do
  defmacro __using__(_opts) do
    quote do
      alias OctoconDiscord.{
        Utils,
        Emojis
      }

      alias Nostrum.Struct.{
        Embed,
        Emoji
      }

      alias Nostrum.Struct.Component.{
        Button
      }

      def map_emoji(%Nostrum.Struct.Emoji{id: id, name: name}) do
        %{id: id, name: name}
      end

      def back_button(page, uid) do
        Button.interaction_button(
          "Back",
          "help|nav-#{page}|#{uid}",
          emoji: Emojis.backarrow(),
          style: 2
        )
      end
    end
  end
end
