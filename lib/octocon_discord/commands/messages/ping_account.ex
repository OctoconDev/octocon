defmodule OctoconDiscord.Commands.Messages.PingAccount do
  @moduledoc false

  @behaviour Nosedrum.ApplicationCommand

  alias OctoconDiscord.Utils

  alias Octocon.Messages

  @impl true
  def description, do: "Pings the account associated with a proxied message."

  @impl true
  def command(interaction) do
    %{
      data: %{
        resolved: %Nostrum.Struct.ApplicationCommandInteractionDataResolved{
          messages: messages
        }
      },
      user: %{
        id: user_id
      },
      guild_id: guild_id,
      channel_id: channel_id
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
            "This message either:\n\n- Was not proxied by Octocon.\n- Is more than 6 months old."
          )

        message ->
          permalink = "https://discord.com/channels/#{guild_id}/#{channel_id}/#{message_id}"
          [
            content: "<@#{message.author_id}>",
            embeds: [
              %Nostrum.Struct.Embed{
                color: Utils.hex_to_int("#0FBEAA"),
                title: ":bell: You've been pinged!",
                description: "<@#{user_id}> has pinged you from a [proxied message](#{permalink}).",
              }
            ],
            ephemeral?: false
          ]
      end
    else
      Utils.error_embed("You can only do this with messages proxied by Octocon.")
    end
  end

  @impl true
  def type, do: :message
end
