defmodule OctoconDiscord.Commands.Alter do
  @moduledoc false

  @behaviour Nosedrum.ApplicationCommand

  import OctoconDiscord.Utils, only: [with_id_or_alias: 2]

  alias OctoconDiscord.Components.AlterPaginator

  alias Octocon.{
    Accounts,
    Alters
  }

  alias OctoconDiscord.Utils

  @subcommands %{
    "create" => &__MODULE__.create/2,
    "delete" => &__MODULE__.delete/2,
    "view" => &__MODULE__.view/2,
    "avatar" => &__MODULE__.Avatar.command/2,
    "remove-alias" => &__MODULE__.remove_alias/2,
    "remove-proxy-name" => &__MODULE__.remove_proxy_name/2,
    "extra-images" => &__MODULE__.extra_images/2,
    "security" => &__MODULE__.security/2,
    "list" => &__MODULE__.list/2,
    "edit" => &__MODULE__.edit/2,
    "proxy" => &__MODULE__.Proxy.command/2
  }

  @impl true
  def description, do: "Manages your system's alters."

  @impl true
  def command(interaction) do
    %{data: %{resolved: resolved}, user: %{id: discord_id}} = interaction
    discord_id = to_string(discord_id)

    Utils.ensure_registered(discord_id, fn ->
      %{data: %{options: [%{name: name, options: options}]}} = interaction

      @subcommands[name].(
        %{resolved: resolved, system_identity: {:discord, discord_id}, discord_id: discord_id},
        options
      )
    end)
  end

  def create(%{system_identity: system_identity}, options) do
    name = Utils.get_command_option(options, "name")

    case Alters.create_alter(system_identity, %{name: name}) do
      {:ok, id, _} ->
        Utils.success_embed(
          "Successfully created alter **#{name}**! Their ID is **#{id}**. You can view their profile with `/alter view #{id}`.\n\n**Note:** This alter is currently private. You can change this with `/alter security #{id}`."
        )

      {:error, _} ->
        Utils.error_embed(
          "Whoops! An unknown error occurred while creating the alter. Please try again."
        )
    end
  end

  def delete(%{system_identity: system_identity}, options) do
    with_id_or_alias(options, fn alter_identity ->
      case Alters.delete_alter(system_identity, alter_identity) do
        :ok ->
          Utils.success_embed(
            "Successfully deleted alter with ID/alias **#{elem(alter_identity, 1)}**!"
          )

        {:error, :no_alter_id} ->
          Utils.error_embed("You don't have an alter with ID **#{elem(alter_identity, 1)}**.")

        {:error, :no_alter_alias} ->
          Utils.error_embed("You don't have an alter with alias **#{elem(alter_identity, 1)}**.")

        {:error, _} ->
          Utils.error_embed(
            "An unknown error occurred while deleting the alter. Please try again."
          )
      end
    end)
  end

  def view(%{system_identity: system_identity}, options) do
    with_id_or_alias(options, fn alter_identity ->
      case Alters.get_alter_by_id(system_identity, alter_identity) do
        {:ok, alter} ->
          [
            embeds: [Utils.alter_embed(alter)],
            ephemeral?: true
          ]

        {:error, :no_alter_id} ->
          Utils.error_embed("You don't have an alter with ID **#{elem(alter_identity, 1)}**.")

        {:error, :no_alter_alias} ->
          Utils.error_embed("You don't have an alter with alias **#{elem(alter_identity, 1)}**.")
      end
    end)
  end

  def list(%{system_identity: system_identity}, options) do
    system_id = Accounts.id_from_system_identity(system_identity, :system)

    sort =
      case Utils.get_command_option(options, "sort") do
        nil -> :id
        "id" -> :id
        "alphabetical" -> :alphabetical
      end

    alters =
      Alters.get_alters_by_id({:system, system_id}, [
        :id,
        :name,
        :pronouns,
        :discord_proxies,
        :alias
      ])

    sorted_alters =
      case sort do
        :id -> alters
        :alphabetical -> alters |> Enum.sort_by(& &1.name)
      end

    AlterPaginator.handle_init(system_id, sorted_alters, length(sorted_alters))
  end

  def update_alter(
        %{system_identity: system_identity},
        alter_identity,
        options,
        success_text,
        embed_alter \\ true
      ) do
    case options do
      map when map_size(map) == 0 ->
        Utils.error_embed("You must provide at least one field to update.")

      _ ->
        case Alters.update_alter(system_identity, alter_identity, options) do
          :ok ->
            [
              embeds:
                if embed_alter do
                  [
                    Utils.success_embed_raw(success_text),
                    Utils.alter_embed(Alters.get_alter_by_id!(system_identity, alter_identity))
                  ]
                else
                  [Utils.success_embed_raw(success_text)]
                end,
              ephemeral?: true
            ]

          {:error, :no_alter_id} ->
            Utils.error_embed("You don't have an alter with ID **#{elem(alter_identity, 1)}**.")

          {:error, :no_alter_alias} ->
            Utils.error_embed(
              "You don't have an alter with alias **#{elem(alter_identity, 1)}**."
            )

          {:error, _} ->
            Utils.error_embed(
              "An unknown error occurred while updating the alter. Please try again."
            )
        end
    end
  end

  def edit(%{system_identity: system_identity} = context, options) do
    with_id_or_alias(options, fn alter_identity ->
      to_update =
        %{
          name: Utils.get_command_option(options, "name"),
          pronouns: Utils.get_command_option(options, "pronouns"),
          description: Utils.get_command_option(options, "description"),
          proxy_name: Utils.get_command_option(options, "proxy-name"),
          color: Utils.get_command_option(options, "color"),
          alias: Utils.get_command_option(options, "alias")
        }
        |> Map.filter(fn {_, v} -> v != nil end)

      # This is ugly, but it works.
      try do
        if Map.has_key?(to_update, :alias) do
          if Alters.alias_taken?(system_identity, to_update[:alias]) do
            throw("You already have an alter with the alias **#{to_update[:alias]}**.")
          end

          case Utils.validate_alias(to_update[:alias]) do
            {:error, error} -> throw(error)
            {:alias, _} -> :ok
          end
        end

        if(Map.has_key?(to_update, :color)) do
          case Utils.validate_hex_color(to_update[:color]) do
            :error ->
              throw("Invalid color. Please provide a valid hex code.")

            {:ok, new_color} ->
              update_alter(
                context,
                alter_identity,
                Map.put(to_update, :color, "#" <> new_color),
                "Successfully edited alter with ID/alias **#{elem(alter_identity, 1)}**!"
              )
          end
        else
          update_alter(
            context,
            alter_identity,
            to_update,
            "Successfully edited alter with ID/alias **#{elem(alter_identity, 1)}**!"
          )
        end
      catch
        e -> Utils.error_embed(e)
      end
    end)
  end

  def security(context, options) do
    with_id_or_alias(options, fn alter_identity ->
      security_level = Utils.get_command_option(options, "level") |> String.to_existing_atom()

      update_alter(
        context,
        alter_identity,
        %{security_level: security_level},
        "Successfully updated alter's security level!",
        true
      )
    end)
  end

  def remove_alias(context, options) do
    with_id_or_alias(options, fn alter_identity ->
      update_alter(
        context,
        alter_identity,
        %{alias: nil},
        "Successfully removed alias from alter!",
        false
      )
    end)
  end

  def remove_proxy_name(context, options) do
    with_id_or_alias(options, fn alter_identity ->
      update_alter(
        context,
        alter_identity,
        %{proxy_name: nil},
        "Successfully removed proxy name from alter!",
        false
      )
    end)
  end

  def extra_images(_context, _options) do
    Utils.error_embed("This command is not yet implemented.")
  end

  @impl true
  def type, do: :slash

  @impl true
  def options,
    do: [
      %{
        name: "create",
        description: "Creates a new alter.",
        type: :sub_command,
        options: [
          %{
            name: "name",
            type: :string,
            max_length: 80,
            description: "The name of the alter to create.",
            required: false
          }
        ]
      },
      %{
        name: "delete",
        description: "Deletes an existing alter.",
        type: :sub_command,
        options: [
          %{
            name: "id",
            type: :string,
            max_length: 80,
            description: "The ID (or alias) of the alter to delete.",
            required: true
          }
        ]
      },
      %{
        name: "view",
        description: "Views an existing alter.",
        type: :sub_command,
        options: [
          %{
            name: "id",
            type: :string,
            max_length: 80,
            description: "The ID (or alias) of the alter to view.",
            required: true
          }
        ]
      },
      %{
        name: "list",
        description: "Lists all of your alters.",
        type: :sub_command,
        options: [
          %{
            name: "sort",
            description: "The method to sort your alters by.",
            type: :string,
            choices: [
              %{name: "ID", value: "id"},
              %{name: "Alphabetical", value: "alphabetical"}
            ]
          }
        ]
      },
      %{
        name: "security",
        description: "Manages an alter's security level.",
        type: :sub_command,
        options: [
          %{
            name: "id",
            type: :string,
            description: "The ID (or alias) of the alter to update.",
            max_length: 80,
            required: true
          },
          %{
            name: "level",
            type: :string,
            description: "The security level to set the alter to.",
            required: true,
            choices: [
              %{name: "Private", value: "private"},
              %{name: "Trusted friends only", value: "trusted_only"},
              %{name: "Friends only", value: "friends_only"},
              %{name: "Public", value: "public"}
            ]
          }
        ]
      },
      %{
        name: "avatar",
        description: "Manages an alter's avatar.",
        type: :sub_command_group,
        options: [
          %{
            name: "set",
            description: "Sets an alter's avatar to the attached image.",
            type: :sub_command,
            options: [
              %{
                name: "id",
                type: :string,
                max_length: 80,
                description: "The ID (or alias) of the alter to set the avatar of.",
                required: true
              },
              %{
                name: "avatar",
                type: :attachment,
                description: "The image to set.",
                required: true
              }
            ]
          },
          %{
            name: "remove",
            description: "Removes an alter's avatar.",
            type: :sub_command,
            options: [
              %{
                name: "id",
                type: :string,
                description: "The ID (or alias) of the alter to remove the avatar of.",
                max_length: 80,
                required: true
              }
            ]
          }
        ]
      },
      %{
        name: "proxy",
        description: "Manages an alter's chat proxies.",
        type: :sub_command_group,
        options: [
          %{
            name: "set",
            description: "Removes all existing proxies and sets a new proxy.",
            type: :sub_command,
            options: [
              %{
                name: "id",
                type: :string,
                max_length: 80,
                description: "The ID (or alias) of the alter to set the proxy of.",
                required: true
              },
              %{
                name: "prefix",
                type: :string,
                max_length: 30,
                description: "The prefix of the proxy to set.",
                required: false
              },
              %{
                name: "suffix",
                type: :string,
                max_length: 30,
                description: "The suffix of the proxy to set.",
                required: false
              }
            ]
          },
          %{
            name: "add",
            description: "Adds a new proxy to an alter.",
            type: :sub_command,
            options: [
              %{
                name: "id",
                type: :string,
                description: "The ID (or alias) of the alter to add a new proxy to.",
                max_length: 80,
                required: true
              },
              %{
                name: "prefix",
                type: :string,
                max_length: 30,
                description: "The prefix of the proxy to add.",
                required: false
              },
              %{
                name: "suffix",
                type: :string,
                max_length: 30,
                description: "The suffix of the proxy to add.",
                required: false
              }
            ]
          },
          %{
            name: "remove",
            description: "Removes a proxy from an alter.",
            type: :sub_command,
            options: [
              %{
                name: "id",
                type: :string,
                description: "The ID (or alias) of the alter to remove the proxy from.",
                max_length: 80,
                required: true
              },
              %{
                name: "prefix",
                type: :string,
                max_length: 30,
                description: "The prefix of the proxy to remove.",
                required: false
              },
              %{
                name: "suffix",
                type: :string,
                max_length: 30,
                description: "The suffix of the proxy to remove.",
                required: false
              }
            ]
          },
          %{
            name: "clear",
            description: "Clears all proxies from an alter.",
            type: :sub_command,
            options: [
              %{
                name: "id",
                type: :string,
                description: "The ID (or alias) of the alter to clear the proxies from.",
                max_length: 80,
                required: true
              }
            ]
          }
        ]
      },
      # %{
      #   name: "extra-images",
      #   description: "Manages an alter's extra images.",
      #   type: :sub_command_group,
      #   options: [
      #     %{
      #       name: "add-url",
      #       description: "Adds an extra image to an alter with a URL.",
      #       type: :sub_command,
      #       options: [
      #         %{
      #           name: "id",
      #           type: :integer,
      #           description: "The ID of the alter to add an extra image to.",
      #           required: true
      #         },
      #         %{
      #           name: "url",
      #           type: :string,
      #           max_length: 2000,
      #           description: "The URL of the extra image to add.",
      #           required: true
      #         }
      #       ]
      #     },
      #     %{
      #       name: "add",
      #       description: "Adds an extra image to an alter with an attachment.",
      #       type: :sub_command,
      #       options: [
      #         %{
      #           name: "id",
      #           type: :integer,
      #           description: "The ID of the alter to add an extra image to.",
      #           required: true
      #         },
      #         %{
      #           name: "image",
      #           type: :attachment,
      #           description: "The image to add.",
      #           required: true
      #         }
      #       ]
      #     },
      #     %{
      #       name: "remove",
      #       description: "Removes an extra image from an alter.",
      #       type: :sub_command,
      #       options: [
      #         %{
      #           name: "id",
      #           type: :integer,
      #           description: "The ID of the alter to remove an extra image from.",
      #           required: true
      #         },
      #         %{
      #           name: "index",
      #           type: :integer,
      #           description: "The index of the extra image to remove.",
      #           required: true
      #         }
      #       ]
      #     },
      #     %{
      #       name: "list",
      #       description: "Lists an alter's extra images.",
      #       type: :sub_command,
      #       options: [
      #         %{
      #           name: "id",
      #           type: :integer,
      #           description: "The ID of the alter to list extra images from.",
      #           required: true
      #         }
      #       ]
      #     }
      #   ]
      # },
      %{
        name: "remove-alias",
        description: "Removes an alter's alias.",
        type: :sub_command,
        options: [
          %{
            name: "id",
            type: :string,
            description: "The ID (or alias) of the alter to remove the alias from.",
            max_length: 80,
            required: true
          }
        ]
      },
      %{
        name: "remove-proxy-name",
        description: "Removes an alter's proxy name.",
        type: :sub_command,
        options: [
          %{
            name: "id",
            type: :string,
            description: "The ID (or alias) of the alter to remove the proxy name from.",
            max_length: 80,
            required: true
          }
        ]
      },
      %{
        name: "edit",
        description: "Edits an existing alter.",
        type: :sub_command,
        options: [
          %{
            name: "id",
            type: :string,
            description: "The ID (or alias) of the alter to update.",
            max_length: 80,
            required: true
          },
          %{
            name: "name",
            type: :string,
            max_length: 80,
            description: "The new name of the alter.",
            required: false
          },
          %{
            name: "pronouns",
            type: :string,
            max_length: 50,
            description: "The new pronouns of the alter.",
            required: false
          },
          %{
            name: "description",
            type: :string,
            max_length: 3000,
            description: "The new description of the alter.",
            required: false
          },
          %{
            name: "proxy-name",
            type: :string,
            max_length: 80,
            description: "The new proxy name of the alter.",
            required: false
          },
          %{
            name: "color",
            type: :string,
            min_length: 6,
            max_length: 7,
            description: "The new color (hex code) of the alter.",
            required: false
          },
          %{
            name: "alias",
            type: :string,
            max_length: 80,
            description: "The new alias of the alter.",
            required: false
          }
        ]
      }
    ]
end
