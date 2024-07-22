defmodule OctoconDiscord.Commands.Alter.Avatar do
  @moduledoc false
  import Octocon.Utils.Alter, only: [upload_avatar: 3]

  alias Octocon.{
    Accounts,
    Alters
  }

  alias OctoconDiscord.Utils

  import OctoconDiscord.Utils, only: [with_id_or_alias: 2]

  @subcommands %{
    "set" => &__MODULE__.set_attachment/2,
    # "set-url" => &__MODULE__.set_url/2,
    "remove" => &__MODULE__.remove/2
  }

  def command(%{system_identity: system_identity} = context, options) do
    subcommand = hd(options)

    with_id_or_alias(subcommand.options, fn alter_identity ->
      alter_id = Alters.resolve_alter(system_identity, alter_identity)

      if alter_id != false do
        @subcommands[subcommand.name].(
          context
          |> Map.put(:alter_identity, {:id, alter_id}),
          subcommand.options
        )
      else
        case alter_identity do
          {:id, alter_id} ->
            Utils.error_embed("You don't have an alter with ID **#{alter_id}**.")

          {:alias, aliaz} ->
            Utils.error_embed("You don't have an alter with alias **#{aliaz}**.")
        end
      end
    end)
  end

  def set_attachment(
        %{resolved: resolved, system_identity: system_identity, alter_identity: alter_identity},
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
        case upload_avatar(system_identity, alter_identity, attachment.url) do
          :ok ->
            Utils.success_embed("Successfully set alter's avatar!")

          _ ->
            Utils.error_embed(
              "An unknown error occurred while processing the image. Please try again."
            )
        end
    end
  end

  # def set_url(_context, _options) do
  #   Utils.error_embed("This command is not yet implemented.")
  # end

  def remove(
        %{system_identity: system_identity, alter_identity: alter_identity} = context,
        _options
      ) do
    result =
      OctoconDiscord.Commands.Alter.update_alter(
        context,
        alter_identity,
        %{avatar_url: ""},
        "Successfully removed alter's avatar!",
        false
      )

    spawn(fn ->
      system_id = Accounts.id_from_system_identity(system_identity, :system)
      alter_id = Alters.resolve_alter(system_identity, alter_identity)
      Octocon.Utils.nuke_existing_avatars!(system_id, alter_id)
    end)

    result
  end
end
