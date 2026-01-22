defmodule OctoconDiscord.Commands.Messages.Reproxy do
  @moduledoc false

  @behaviour Nosedrum.ApplicationCommand

  alias Octocon.Messages
  alias OctoconDiscord.Utils

  @impl Nosedrum.ApplicationCommand
  def description, do: "Reproxies a message."

  @impl Nosedrum.ApplicationCommand
  def command(%{
        data: %{
          resolved: %Nostrum.Struct.ApplicationCommandInteractionDataResolved{
            messages: messages
          }
        },
        user: %{id: user_id},
        guild_id: guild_id
      }) do
    [
      {message_id,
       %Nostrum.Struct.Message{
         author: %Nostrum.Struct.User{
           bot: is_bot
         }
       } = raw_message}
    ] =
      messages
      |> Enum.map(& &1)

    if is_bot do
      case Messages.lookup_message(message_id) do
        nil ->
          Utils.error_embed(
            "This message either:\n\n- Was not proxied by Octocon.\n- Is more than 6 months old."
          )

        db_message ->
          try_reproxy_message(
            user_id,
            %Nostrum.Struct.Message{raw_message | guild_id: guild_id},
            db_message
          )
      end
    else
      Utils.error_embed("You can only do this with messages proxied by Octocon.")
    end
  end

  defp try_reproxy_message(user_id, raw_message, db_message) do
    if db_message.author_id == to_string(user_id) do
      OctoconDiscord.Components.ReproxyHandler.handle_init(user_id, raw_message, db_message)
    else
      Utils.error_embed("You can only reproxy your own messages.")
    end
  end

  @impl Nosedrum.ApplicationCommand
  def type, do: :message

  # @impl Nosedrum.ApplicationCommand
  # def options, do: []
end
