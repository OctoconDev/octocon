defmodule OctoconDiscord.Commands.Messages.WhoIsThis do
  @moduledoc false

  @behaviour Nosedrum.ApplicationCommand

  alias OctoconDiscord.Utils

  alias Octocon.{
    Accounts,
    Alters,
    Messages
  }

  @impl true
  def description, do: "Displays information about a proxied message."

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
          display_message_data(to_string(user_id), message)
      end
    else
      Utils.error_embed("You can only look up messages proxied by Octocon.")
    end
  end

  defp display_message_data(user_id, %Messages.Message{} = message) do
    case Accounts.get_user({:discord, message.author_id}) do
      nil ->
        Utils.error_embed(
          "This user's Octocon account was deleted.\n\nHowever, this message was sent by the following Discord user: <@#{message.author_id}>\n\nWho had the following system ID: `#{message.system_id}`"
        )

      target_user ->
        caller_user = Accounts.get_user({:discord, user_id})

        [
          embeds:
            [
              generate_system_embed(target_user, caller_user)
            ] ++ maybe_alter_embed(target_user.id, message.alter_id, caller_user),
          ephemeral?: true
        ]
    end
  end

  defp generate_system_embed(target_user, nil) do
    Utils.system_embed_raw(target_user, false)
  end

  defp generate_system_embed(target_user, caller_user) do
    Utils.system_embed_raw(target_user, target_user.id == caller_user.id)
  end

  def maybe_alter_embed(system_id, alter_id, caller_user) when caller_user.id == system_id do
    case Alters.get_alter_by_id({:system, system_id}, {:id, alter_id}) do
      {:error, _} -> []
      {:ok, alter} -> [Utils.alter_embed(alter, false)]
    end
  end

  def maybe_alter_embed(system_id, alter_id, caller_user) do
    caller_identity =
      case caller_user do
        nil -> nil
        user -> {:system, user.id}
      end

    case Alters.get_alter_guarded({:system, system_id}, {:id, alter_id}, caller_identity) do
      :error -> []
      {:ok, alter} -> [Utils.alter_embed(alter, true)]
    end
  end

  @impl true
  def type, do: :message
end
