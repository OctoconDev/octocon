defmodule OctoconDiscord.Commands.Autoproxy do
  @moduledoc false

  @behaviour Nosedrum.ApplicationCommand

  alias OctoconDiscord.ProxyCache
  alias Octocon.Accounts
  alias OctoconDiscord.Utils

  @autoproxy_warning "\n\n**WARNING**: Autoproxy in Octocon is **global**, meaning that it's shared across all Discord servers!"

  @autoproxy_descriptions %{
    off: "Autoproxying is now disabled.",
    front:
      "You will now automatically proxy as your current **primary fronter** if applicable. If not, you will proxy as the **longest current fronter**.#{@autoproxy_warning}",
    latch:
      "You will now automatically proxy as the last alter to send a message. *This will take effect the next time you proxy.*#{@autoproxy_warning}"
  }

  @impl true
  def description, do: "Changes your autoproxy settings."

  @impl true
  def command(interaction) do
    %{id: discord_id} = interaction.user
    discord_id = to_string(discord_id)

    Utils.ensure_registered(discord_id, fn ->
      %{data: %{options: options}} = interaction
      system_identity = {:discord, discord_id}

      mode = Utils.get_command_option(options, "mode")
      mode_atom = String.to_existing_atom(mode)

      # This atom cast should be safe because Discord should only send us valid options
      case Accounts.update_user_by_system_identity(system_identity, %{autoproxy_mode: mode_atom}) do
        {:ok, _} ->
          update_autoproxy_cache(discord_id, mode_atom)

          Utils.success_embed(
            "Autoproxy mode set to `#{mode |> String.capitalize()}`.\n\n#{@autoproxy_descriptions[mode_atom]}"
          )

        {:error, _} ->
          Utils.error_embed("An unknown error occurred while updating your autoproxy mode.")
      end
    end)
  end

  defp update_autoproxy_cache(discord_id, mode) do
    case mode do
      :latch ->
        ProxyCache.update_mode(discord_id, {:latch, :ready})

      :front ->
        ProxyCache.update_mode(discord_id, :front)

      _ ->
        ProxyCache.update_mode(discord_id, :none)
    end
  end

  @impl true
  def type, do: :slash

  @impl true
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
