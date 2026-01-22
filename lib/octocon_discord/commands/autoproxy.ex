defmodule OctoconDiscord.Commands.Autoproxy do
  @moduledoc false

  @behaviour Nosedrum.ApplicationCommand

  alias Octocon.Accounts

  alias OctoconDiscord.{
    ProxyCache,
    Utils
  }

  @autoproxy_descriptions %{
    off: "Autoproxying is now disabled.",
    front:
      "You will now automatically proxy as your current **main fronter** if applicable. If not, you will proxy as the **longest current fronter**.",
    latch:
      "You will now automatically proxy as the last alter to send a message. *This will take effect the next time you proxy.*"
  }

  @impl Nosedrum.ApplicationCommand
  def description, do: "Changes your server-specific autoproxy settings."

  @impl Nosedrum.ApplicationCommand
  def command(interaction) do
    %{guild_id: guild_id, user: %{id: discord_id}} = interaction
    guild_id = to_string(guild_id)
    discord_id = to_string(discord_id)

    Utils.ensure_registered(discord_id, fn ->
      %{data: %{options: options}} = interaction
      system_identity = {:discord, discord_id}

      mode = Utils.get_command_option(options, "mode")
      # This atom cast should be safe because Discord should only send us valid options
      mode_atom = String.to_existing_atom(mode)

      new_settings =
        %{
          autoproxy_mode: mode_atom
        }
        |> then(fn settings ->
          if mode_atom != :latch do
            Map.put(settings, :latched_alter, nil)
          else
            settings
          end
        end)

      case Accounts.update_server_settings(system_identity, guild_id, new_settings) do
        {:ok, _} ->
          ProxyCache.invalidate(discord_id)

          Utils.success_embed(
            "Server autoproxy mode set to `#{mode |> String.capitalize()}`.\n\n#{@autoproxy_descriptions[mode_atom]}\n\n⚠️ Please note that this setting only applies to the server you just ran this command in. If you wish to set your autoproxy mode globally, please use `/global-autoproxy`."
          )

        {:error, _} ->
          Utils.error_embed("An unknown error occurred while updating your autoproxy mode.")
      end
    end)
  end

  @impl Nosedrum.ApplicationCommand
  def type, do: :slash

  @impl Nosedrum.ApplicationCommand
  def options,
    do: [
      %{
        name: "mode",
        type: :string,
        description: "The mode to set your autoproxy to.",
        required: true,
        choices: [
          %{name: "off", value: "off"},
          %{name: "front", value: "front"},
          %{name: "latch", value: "latch"}
        ]
      }
    ]
end
