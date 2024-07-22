defmodule OctoconDiscord.Commands.Reproxy do
  @moduledoc false

  @behaviour Nosedrum.ApplicationCommand

  alias OctoconDiscord.LastMessageManager
  alias OctoconDiscord.Utils

  alias Octocon.Alters

  alias Nostrum.Api

  @impl true
  def description, do: "Reproxies (and optionally edits) your last message as the given alter."

  @impl true
  def command(interaction) do
    %{
      user: %{id: discord_id},
      data: %{options: options}
    } = interaction

    discord_id = to_string(discord_id)

    Utils.ensure_registered(discord_id, fn ->
      Utils.with_id_or_alias(options, fn alter_identity ->
        new_text = Utils.get_command_option(options, "text")

        case LastMessageManager.get(discord_id) do
          nil ->
            Utils.error_embed("Couldn't find a message to reproxy. Have you waited too long?")

          cached ->
            case Alters.get_alter_by_id({:discord, discord_id}, alter_identity, [
                   :name,
                   :avatar_url,
                   :pronouns
                 ]) do
              {:ok, alter} ->
                reproxy_message(cached, alter, new_text)
                LastMessageManager.clear(discord_id)

                [
                  ephemeral?: true
                ]

              {:error, :no_alter_id} ->
                Utils.error_embed(
                  "You don't have an alter with ID **#{elem(alter_identity, 1)}**."
                )

              {:error, :no_alter_alias} ->
                Utils.error_embed(
                  "You don't have an alter with alias **#{elem(alter_identity, 1)}**."
                )
            end
        end
      end)
    end)
  end

  defp reproxy_message({message_id, channel_id, content, embeds}, alter, new_text) do
    webhook = OctoconDiscord.WebhookManager.get_webhook(channel_id)

    unless webhook == nil do
      parsed_pronouns =
        case alter.pronouns do
          nil -> ""
          "" -> ""
          pronouns -> " (#{pronouns})"
        end

      webhook_data = %{
        content: if(new_text, do: new_text, else: content),
        username: "#{alter.name}#{parsed_pronouns}",
        avatar_url: alter.avatar_url,
        embeds: embeds
      }

      webhook_task =
        Task.async(fn ->
          Api.execute_webhook(webhook.id, webhook.token, webhook_data, false)
        end)

      delete_task =
        Task.async(fn ->
          Api.delete_message!(channel_id, message_id)
        end)

      Task.await_many([webhook_task, delete_task], :infinity)
    end
  end

  @impl true
  def type, do: :slash

  @impl true
  def options,
    do: [
      %{
        name: "id",
        max_length: 80,
        description: "The ID (or alias) of the alter to reproxy as.",
        type: :string,
        required: true
      },
      %{
        name: "text",
        description: "The text to edit the message to.",
        type: :string,
        required: false
      }
    ]
end
