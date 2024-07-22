defmodule OctoconDiscord.Commands.Danger do
  @moduledoc false

  @behaviour Nosedrum.ApplicationCommand

  alias OctoconDiscord.Components.{
    DeleteAccountHandler,
    WipeAltersHandler
  }

  alias OctoconDiscord.Utils

  @subcommands %{
    "wipe-alters" => &__MODULE__.wipe_alters/2,
    "delete-account" => &__MODULE__.delete_account/2
  }

  @impl true
  def description,
    do: "Danger zone! These commands are irreversible and should be used with caution."

  @impl true
  def command(interaction) do
    %{data: %{resolved: resolved}, user: %{id: discord_id}} = interaction
    discord_id = to_string(discord_id)

    Utils.ensure_registered(discord_id, fn ->
      %{data: %{options: [%{name: name, options: options}]}} = interaction

      @subcommands[name].(
        %{resolved: resolved, system_identity: {:discord, discord_id}},
        options
      )
    end)
  end

  def delete_account(%{system_identity: system_identity}, _options) do
    DeleteAccountHandler.handle_init(system_identity)

    Utils.success_embed("Check your DMs!")
  end

  def wipe_alters(%{system_identity: system_identity}, _options) do
    WipeAltersHandler.handle_init(system_identity)

    Utils.success_embed("Check your DMs!")
  end

  @impl true
  def type, do: :slash

  @impl true
  def options,
    do: [
      %{
        name: "delete-account",
        description: "WARNING: Deletes your system and all associated data.",
        type: :sub_command
      },
      %{
        name: "wipe-alters",
        description:
          "WARNING: Wipes all alters from your system, but keeps your account and settings.",
        type: :sub_command
      }
    ]
end
