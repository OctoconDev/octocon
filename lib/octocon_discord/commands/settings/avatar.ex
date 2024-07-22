defmodule OctoconDiscord.Commands.Settings.Avatar do
  @moduledoc false
  import Octocon.Utils.User, only: [upload_avatar: 2]

  alias Octocon.Accounts

  alias OctoconDiscord.Utils

  @subcommands %{
    "set" => &__MODULE__.set_attachment/2,
    "set-url" => &__MODULE__.set_url/2,
    "remove" => &__MODULE__.remove/2
  }

  def command(%{system_identity: system_identity} = context, options) do
    subcommand = hd(options)
    system = Accounts.get_user(system_identity)

    @subcommands[subcommand.name].(
      context
      |> Map.put(:system, system),
      subcommand.options
    )
  end

  def set_attachment(
        %{resolved: resolved, system: system},
        options
      ) do
    avatar_id = Utils.get_command_option(options, "avatar")
    attachment = resolved.attachments[avatar_id]

    cond do
      attachment.height == nil or attachment.width == nil ->
        Utils.error_embed(
          "That file doesn't appear to be a valid image. Please provide an image under 20 MB."
        )

      attachment.size > 20_000_000 ->
        Utils.error_embed(
          "The image you provided is too large. Please provide an image that is less than 20 MB."
        )

      true ->
        case upload_avatar(system, attachment.url) do
          {:ok, _} ->
            Utils.success_embed("Your avatar has been updated.")

          {:error, _} ->
            Utils.error_embed("An unknown error occurred updating your avatar. Please try again.")
        end
    end
  end

  def set_url(_context, _options) do
    Utils.error_embed("This command is not yet implemented.")
  end

  def remove(%{system: system}, _options) do
    case Accounts.update_user(system, %{avatar_url: nil}) do
      {:ok, _} ->
        Octocon.Utils.nuke_existing_avatars!(system.id, "self")
        Utils.success_embed("Your avatar has been removed.")

      {:error, _} ->
        Utils.error_embed("An unknown error occurred removing your avatar. Please try again.")
    end
  end
end
