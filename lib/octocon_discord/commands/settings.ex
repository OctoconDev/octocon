defmodule OctoconDiscord.Commands.Settings do
  @moduledoc false

  @behaviour Nosedrum.ApplicationCommand

  alias Octocon.Accounts

  alias Octocon.Workers.{
    PluralKitImportWorker,
    SimplyPluralImportWorker
  }

  alias OctoconDiscord.{
    ProxyCache,
    Utils
  }

  @subcommands %{
    "username" => &__MODULE__.username/2,
    "remove-username" => &__MODULE__.remove_username/2,
    "avatar" => &__MODULE__.Avatar.command/2,
    "system-tag" => &__MODULE__.system_tag/2,
    "remove-system-tag" => &__MODULE__.remove_system_tag/2,
    "show-system-tag" => &__MODULE__.show_system_tag/2,
    "proxy-case-sensitivity" => &__MODULE__.proxy_case_sensitivity/2,
    "proxy-show-pronouns" => &__MODULE__.proxy_show_pronouns/2,
    "ids-as-proxies" => &__MODULE__.ids_as_proxies/2,
    "toggle-proxy-delay" => &__MODULE__.toggle_proxy_delay/2,
    "toggle-silent-proxying" => &__MODULE__.toggle_silent_proxying/2,
    "toggle-proxying" => &__MODULE__.toggle_proxying/2,
    "import-pk" => &__MODULE__.import_pk/2,
    "import-sp" => &__MODULE__.import_sp/2
  }

  @impl Nosedrum.ApplicationCommand
  def description, do: "Modifies your system's settings."

  @impl Nosedrum.ApplicationCommand
  def command(interaction) do
    %{data: %{resolved: resolved}, guild_id: guild_id, user: %{id: discord_id}} = interaction
    discord_id = to_string(discord_id)

    Utils.ensure_registered(discord_id, fn ->
      %{data: %{options: [%{name: name, options: options}]}} = interaction

      @subcommands[name].(
        %{
          resolved: resolved,
          system_identity: {:discord, discord_id},
          discord_id: discord_id,
          guild_id: to_string(guild_id)
        },
        options
      )
    end)
  end

  def username(%{system_identity: system_identity}, options) do
    username = Utils.get_command_option(options, "username")
    user = Accounts.get_user!(system_identity)

    if username == user.username do
      Utils.error_embed("Your username is already set to `#{username}`.")
    else
      case Accounts.update_user(user, %{username: username}) do
        {:ok, _} ->
          Utils.success_embed("Your username has been changed to `#{username}`.")

        {:error,
         %Ecto.Changeset{
           errors: [
             username: {"has already been taken", _}
           ]
         }} ->
          Utils.error_embed("The username `#{username}` is already taken.")

        {:error,
         %Ecto.Changeset{
           errors: [
             username: {"has invalid format", _}
           ]
         }} ->
          Utils.error_embed(
            "The username `#{username}` is invalid. It must satisfy the following criteria:\n\n- Between 5-16 characters\n- Only contains letters, numbers, dashes, and underscores\n- Does not start or end with a symbol\n- Does not consist of seven lowercase letters in a row (like a system ID)"
          )
      end
    end
  end

  def remove_username(%{system_identity: system_identity}, _options) do
    user = Accounts.get_user!(system_identity)

    case Accounts.update_user(user, %{username: nil}) do
      {:ok, _} ->
        Utils.success_embed("Your username has been removed.")

      {:error, _} ->
        Utils.error_embed("An unknown error occurred while removing your username.")
    end
  end

  def system_tag(%{system_identity: system_identity}, options) do
    tag = Utils.get_command_option(options, "tag")

    case Accounts.update_discord_settings(system_identity, %{system_tag: tag}) do
      {:ok, _} ->
        Utils.success_embed("Your system tag has been changed to `#{tag}`.")

      {:error, %Ecto.Changeset{}} ->
        Utils.error_embed(
          "The system tag `#{tag}` is invalid. It must satisfy the following criteria:\n\n- Between 1-8 characters"
        )

      {:error, _} ->
        Utils.error_embed("An unknown error occurred while changing your system tag.")
    end
  end

  def remove_system_tag(%{system_identity: system_identity}, _options) do
    case Accounts.update_discord_settings(system_identity, %{system_tag: nil}) do
      {:ok, _} ->
        Utils.success_embed("Your system tag has been removed.")

      {:error, _} ->
        Utils.error_embed("An unknown error occurred while removing your system tag.")
    end
  end

  def show_system_tag(%{system_identity: system_identity, discord_id: discord_id}, _options) do
    {:ok, %{settings: %{show_system_tag: show_system_tag}}} = ProxyCache.get(discord_id, false)

    new_value = not show_system_tag

    case Accounts.update_discord_settings(system_identity, %{show_system_tag: new_value}) do
      {:ok, _} ->
        Utils.success_embed(
          "Your system tag will now #{if new_value, do: "be", else: "no longer be"} shown when proxying. Servers can override this!"
        )

      {:error, _} ->
        Utils.error_embed("An unknown error occurred while changing your system tag visibility.")
    end
  end

  def proxy_case_sensitivity(
        %{system_identity: system_identity, discord_id: discord_id},
        _options
      ) do
    {:ok, %{settings: %{case_insensitive_proxies: case_insensitive_proxies}}} =
      ProxyCache.get(discord_id, false)

    new_value = not case_insensitive_proxies

    case Accounts.update_discord_settings(system_identity, %{case_insensitive_proxies: new_value}) do
      {:ok, _} ->
        Utils.success_embed(
          "Proxying will now #{if new_value, do: "be", else: "no longer be"} case-insensitive."
        )

      {:error, _} ->
        Utils.error_embed("An unknown error occurred while changing your proxy case sensitivity.")
    end
  end

  def proxy_show_pronouns(%{system_identity: system_identity, discord_id: discord_id}, _options) do
    {:ok, %{settings: %{show_pronouns: show_pronouns}}} = ProxyCache.get(discord_id, false)

    new_value = not show_pronouns

    case Accounts.update_discord_settings(system_identity, %{show_pronouns: new_value}) do
      {:ok, _} ->
        Utils.success_embed(
          "Proxying will now #{if new_value, do: "show", else: "hide"} pronouns."
        )

      {:error, _} ->
        Utils.error_embed(
          "An unknown error occurred while changing your proxy pronoun visibility."
        )
    end
  end

  def ids_as_proxies(%{system_identity: system_identity, discord_id: discord_id}, _options) do
    {:ok, %{settings: %{ids_as_proxies: ids_as_proxies}}} = ProxyCache.get(discord_id, false)

    new_value = not ids_as_proxies

    case Accounts.update_discord_settings(system_identity, %{ids_as_proxies: new_value}) do
      {:ok, _} ->
        Utils.success_embed(
          "Alter IDs and aliases will now #{if new_value, do: "be", else: "no longer be"} automatically used as proxies.#{if new_value, do: "\n\nFor example, the message `1-Hello, world!` will be proxied as the alter with ID `1`, and the message `Atlas-Hello, world!` will be proxied as the alter with alias `Atlas`", else: ""}"
        )

      {:error, _} ->
        Utils.error_embed("An unknown error occurred while changing your IDs as proxies setting.")
    end
  end

  def toggle_proxy_delay(%{system_identity: system_identity, discord_id: discord_id}, _options) do
    {:ok, %{settings: %{use_proxy_delay: use_proxy_delay}}} = ProxyCache.get(discord_id, false)

    new_value = not use_proxy_delay

    case Accounts.update_discord_settings(system_identity, %{use_proxy_delay: new_value}) do
      {:ok, _} ->
        Utils.success_embed(
          if(new_value,
            do:
              "Octocon will now introduce a slight delay before proxying your message. This will prevent a Discord bug that causes duplicate \"ghost\" messages to appear when proxying.",
            else: "Octocon will no longer introduce a slight delay before proxying your message."
          )
        )

      {:error, _} ->
        Utils.error_embed("An unknown error occurred while changing your proxy delay setting.")
    end
  end

  def toggle_silent_proxying(
        %{system_identity: system_identity, discord_id: discord_id},
        _options
      ) do
    {:ok, %{settings: %{silent_proxying: silent_proxying}}} = ProxyCache.get(discord_id, false)

    new_value = not silent_proxying

    case Accounts.update_discord_settings(system_identity, %{silent_proxying: new_value}) do
      {:ok, _} ->
        Utils.success_embed(
          if(new_value,
            do:
              "Octocon will now tell Discord to prevent proxied messages from sending push notifications.",
            else: "Proxied messages will now send push notifications as normal."
          )
        )

      {:error, _} ->
        Utils.error_embed("An unknown error occurred while changing your proxy delay setting.")
    end
  end

  def toggle_proxying(
        %{system_identity: system_identity, discord_id: discord_id, guild_id: guild_id},
        _options
      ) do
    {:ok, %{settings: %{server_settings: server_settings}}} = ProxyCache.get(discord_id, false)

    new_value =
      not (Map.get(server_settings, guild_id, %{proxying_disabled: false})
           |> Map.get(:proxying_disabled))

    case Accounts.update_server_settings(system_identity, guild_id, %{
           proxying_disabled: new_value
         }) do
      {:ok, _} ->
        Utils.success_embed(
          "Proxying has been #{if new_value, do: "disabled", else: "enabled"} in this server."
        )

      _ ->
        Utils.error_embed("An error occurred while disabling proxying.")
    end
  end

  def import_pk(%{system_identity: system_identity}, options) do
    pk_token = Utils.get_command_option(options, "token")

    system_id = Accounts.id_from_system_identity(system_identity, :system)

    PluralKitImportWorker.perform(%{"system_id" => system_id, "pk_token" => pk_token})

    Utils.success_embed(
      "Octocon is attempting to import your alters from PluralKit. This may take a while; check your alters with `/alter list`."
    )
  end

  def import_sp(%{system_identity: system_identity}, options) do
    sp_token = Utils.get_command_option(options, "token")
    system_id = Accounts.id_from_system_identity(system_identity, :system)

    SimplyPluralImportWorker.perform(%{"system_id" => system_id, "sp_token" => sp_token})

    Utils.success_embed(
      "Octocon is attempting to import your alters from Simply Plural. This may take a while; check your alters with `/alter list`."
    )
  end

  @impl Nosedrum.ApplicationCommand
  def type, do: :slash

  @impl Nosedrum.ApplicationCommand
  def options,
    do: [
      %{
        name: "username",
        description: "Changes your system's username.",
        type: :sub_command,
        options: [
          %{
            name: "username",
            description: "The new username.",
            type: :string,
            min_length: 5,
            max_length: 16,
            required: true
          }
        ]
      },
      %{
        name: "remove-username",
        description: "Removes your system's username.",
        type: :sub_command
      },
      %{
        name: "avatar",
        description: "Manages your system-wide avatar (profile picture).",
        type: :sub_command_group,
        options: [
          %{
            name: "set",
            description: "Sets your system-wide avatar to the attached image.",
            type: :sub_command,
            options: [
              %{
                name: "avatar",
                type: :attachment,
                description: "The image to set.",
                required: true
              }
            ]
          },
          # %{
          #   name: "set-url",
          #   description: "Sets your system-wide avatar to the provided URL.",
          #   type: :sub_command,
          #   options: [
          #     %{
          #       name: "avatar",
          #       type: :string,
          #       max_length: 2000,
          #       description: "The URL of the avatar to set.",
          #       required: true
          #     }
          #   ]
          # },
          %{
            name: "remove",
            description: "Removes your system-wide avatar.",
            type: :sub_command
          }
        ]
      },
      %{
        name: "system-tag",
        description: "Changes your system tag.",
        type: :sub_command,
        options: [
          %{
            name: "tag",
            description: "The new system tag.",
            type: :string,
            min_length: 1,
            max_length: 20,
            required: true
          }
        ]
      },
      %{
        name: "remove-system-tag",
        description: "Removes your system tag.",
        type: :sub_command
      },
      %{
        name: "show-system-tag",
        description:
          "Toggles whether your system tag is shown when proxying. Servers can override this!",
        type: :sub_command
      },
      %{
        name: "proxy-case-sensitivity",
        description: "Toggles whether proxying is case-insensitive.",
        type: :sub_command
      },
      %{
        name: "proxy-show-pronouns",
        description:
          "Toggles whether proxies automatically show an alter's pronouns in parentheses.",
        type: :sub_command
      },
      %{
        name: "ids-as-proxies",
        description: "Toggles whether alter IDs and aliases are automatically used as proxies.",
        type: :sub_command
      },
      %{
        name: "toggle-proxy-delay",
        description: "Toggles whether Octocon introduces a slight delay before proxying.",
        type: :sub_command
      },
      %{
        name: "toggle-silent-proxying",
        description: "Toggles whether Octocon tells Discord to prevent push notifications.",
        type: :sub_command
      },
      %{
        name: "toggle-proxying",
        description: "Toggles whether proxying is enabled in this server.",
        type: :sub_command
      },
      %{
        name: "import-pk",
        description: "Imports your alters from PluralKit.",
        type: :sub_command,
        options: [
          %{
            name: "token",
            description: "Your PluralKit token.",
            type: :string,
            required: true
          }
        ]
      },
      %{
        name: "import-sp",
        description: "Imports your alters from Simply Plural.",
        type: :sub_command,
        options: [
          %{
            name: "token",
            description: "Your Simply Plural token (must have \"read\" permissions).",
            type: :string,
            required: true
          }
        ]
      }
    ]
end
