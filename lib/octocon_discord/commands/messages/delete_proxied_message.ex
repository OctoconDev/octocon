defmodule OctoconDiscord.Commands.Messages.DeleteProxiedMessage do
  @moduledoc false

  @behaviour Nosedrum.ApplicationCommand

  alias OctoconDiscord.Utils
  alias Octocon.Messages

  alias Nostrum.Api

  @impl true
  def description, do: "Deletes a proxied message."

  @impl true
  def command(interaction) do
    %{
      data: %{
        resolved: %Nostrum.Struct.ApplicationCommandInteractionDataResolved{
          messages: messages
        }
      },
      channel_id: channel_id,
      user: %{
        id: user_id
      }
    } = interaction

    [
      {message_id,
       %Nostrum.Struct.Message{
         author: %Nostrum.Struct.User{
           bot: is_bot
         }
       }}
    ] =
      messages
      |> Enum.map(& &1)

    if is_bot do
      case Messages.lookup_message(message_id) do
        nil ->
          Utils.error_embed(
            "This message either:\n\n- Was not proxied by Octocon.\n-Is more than 6 months old."
          )

        message ->
          try_delete_message(user_id, channel_id, message)
      end
    else
      Utils.error_embed("You can only delete messages proxied by Octocon.")
    end
  end

  defp try_delete_message(user_id, channel_id, %Messages.Message{} = message) do
    if message.author_id == to_string(user_id) do
      Api.delete_message(channel_id, String.to_integer(message.message_id))
      Utils.success_embed("Message deleted!")
    else
      Utils.error_embed("You can only delete your own messages.")
    end
  end

  @impl true
  def type, do: :message
end
