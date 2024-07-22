defmodule OctoconDiscord.Commands.Register do
  @moduledoc false

  @behaviour Nosedrum.ApplicationCommand

  alias Octocon.Accounts
  alias OctoconDiscord.Utils

  @impl true
  def description, do: "Creates a system under your Discord account."

  @impl true
  def command(interaction) do
    %{
      id: discord_id
      # avatar: avatar_hash
    } = interaction.user

    discord_id = to_string(discord_id)

    if Accounts.user_exists?({:discord, discord_id}) do
      Utils.error_embed("You're already registered.")
    else
      # avatar_url = Utils.get_avatar_url(discord_id, avatar_hash)

      case Accounts.create_user_from_discord(
             discord_id
             # ,%{avatar_url: avatar_url}
           ) do
        {:ok, user} ->
          Utils.success_embed(
            "You're registered! Your system ID is: **#{user.id}**\n\nCheck out our [online guide](https://octocon.app/docs/discord/getting-started) for details on how to get started!"
          )

        {:error, _} ->
          Utils.error_embed(
            "An unknown error occurred while registering your system. Please try again."
          )
      end
    end
  end

  @impl true
  def type, do: :slash

  # @impl true
  # def options, do: []
end
