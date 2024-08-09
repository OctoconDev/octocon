defmodule OctoconDiscord.Commands.Front do
  @moduledoc false

  @behaviour Nosedrum.ApplicationCommand

  alias Octocon.{
    Accounts,
    Alters,
    Fronts
  }

  alias OctoconDiscord.Utils

  import OctoconDiscord.Utils, only: [with_id_or_alias: 2]

  @subcommands %{
    "set" => &__MODULE__.set/2,
    "end" => &__MODULE__.endd/2,
    "add" => &__MODULE__.add/2,
    "view" => &__MODULE__.view/2,
    "primary" => &__MODULE__.primary/2,
    "remove-primary" => &__MODULE__.remove_primary/2
  }

  @impl true
  def description, do: "Manages which alters are in front."

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

  def view(%{system_identity: system_identity}, _options) do
    currently_fronting = Fronts.currently_fronting(system_identity)

    if currently_fronting == [] do
      Utils.error_embed(
        "No alters are fronting! Use `/front add` to add alters to front, or `/front set` to set a single alter to front."
      )
    else
      now_epoch = Timex.Duration.now(:second)

      [
        embeds: [
          %Nostrum.Struct.Embed{
            title: "Currently fronting alters (#{length(currently_fronting)})",
            description:
              Enum.map_join(currently_fronting, "\n", fn %{
                                                           front: front,
                                                           alter: alter,
                                                           primary: primary
                                                         } ->
                start_epoch = front.time_start |> Timex.to_unix()

                "- `#{alter.id}  ` **#{alter.name}** #{case front.comment do
                  [] -> ""
                  "" -> ""
                  comment -> "(#{comment})"
                end}#{if primary,
                  do: " :star:",
                  else: ""}\n  - *#{(now_epoch - start_epoch) |> Timex.Duration.from_seconds() |> Timex.format_duration(:humanized)}*\n"
              end),
            footer: %Nostrum.Struct.Embed.Footer{
              text: "⭐ = Primary front"
            }
          }
        ],
        ephemeral?: true
      ]
    end
  end

  def set(%{system_identity: system_identity}, options) do
    with_id_or_alias(options, fn alter_identity ->
      alter_id = Alters.resolve_alter(system_identity, alter_identity)

      if alter_id != false do
        comment = Utils.get_command_option(options, "comment") || ""
        set_primary? = Utils.get_command_option(options, "set-primary") || false

        case Fronts.set_front(system_identity, alter_identity, comment) do
          {:ok, _} ->
            if set_primary? do
              Accounts.set_primary_front(system_identity, alter_id)
            end

            Utils.success_embed(
              "This alter is now fronting. All other alters have been removed from front."
            )

          {:error, :already_fronting} ->
            Utils.error_embed("This alter is already fronting.")

          {:error, _} ->
            Utils.error_embed("An unknown error occurred.")
        end
      else
        case alter_identity do
          {:id, id} ->
            Utils.error_embed("You don't have an alter with ID **#{id}**.")

          {:alias, aliaz} ->
            Utils.error_embed("You don't have an alter with alias **#{aliaz}**.")
        end
      end
    end)
  end

  def add(%{system_identity: system_identity}, options) do
    with_id_or_alias(options, fn alter_identity ->
      alter_id = Alters.resolve_alter(system_identity, alter_identity)

      if alter_id != false do
        comment = Utils.get_command_option(options, "comment") || ""
        set_primary? = Utils.get_command_option(options, "set-primary") || false

        case Fronts.start_front(system_identity, alter_identity, comment) do
          {:ok, _} ->
            if set_primary? do
              Accounts.set_primary_front(system_identity, alter_id)
            end

            Utils.success_embed("This alter is now fronting.")

          {:error, :already_fronting} ->
            Utils.error_embed("This alter is already fronting.")

          {:error, _} ->
            Utils.error_embed("An unknown error occurred.")
        end
      else
        case alter_identity do
          {:id, id} ->
            Utils.error_embed("You don't have an alter with ID **#{id}**.")

          {:alias, aliaz} ->
            Utils.error_embed("You don't have an alter with alias **#{aliaz}**.")
        end
      end
    end)
  end

  def endd(%{system_identity: system_identity}, options) do
    with_id_or_alias(options, fn alter_identity ->
      alter_id = Alters.resolve_alter(system_identity, alter_identity)

      if alter_id != false do
        case Fronts.end_front(system_identity, alter_identity) do
          :ok ->
            Utils.success_embed("Alter with ID **#{alter_id}** was removed from front.")

          {:error, :not_fronting} ->
            Utils.error_embed("Alter with ID **#{alter_id}** is not currently fronting.")

          {:error, _} ->
            Utils.error_embed("An unknown error occurred.")
        end
      else
        case alter_identity do
          {:id, id} ->
            Utils.error_embed("You don't have an alter with ID **#{id}**.")

          {:alias, aliaz} ->
            Utils.error_embed("You don't have an alter with alias **#{aliaz}**.")
        end
      end
    end)
  end

  def primary(%{system_identity: system_identity}, options) do
    with_id_or_alias(options, fn alter_identity ->
      # TODO: Alters.exists?/2
      alter_id = Alters.resolve_alter(system_identity, alter_identity)

      if alter_id != false do
        if Fronts.is_fronting?(system_identity, alter_identity) do
          Accounts.set_primary_front(system_identity, alter_id)

          Utils.success_embed("Set alter as primary front.")
        else
          should_front = Utils.get_command_option(options, "add-to-front") || false

          if should_front do
            add(%{system_identity: system_identity}, [
              %Nostrum.Struct.ApplicationCommandInteractionDataOption{
                name: "set-primary",
                value: true,
                type: 5
              }
              | options
            ])
          else
            Utils.error_embed(
              "That alter is not currently fronting.\n\n-# Hint: rerun this command with the `add-to-front` option to add the alter to front *and* set them as primary in one go!"
            )
          end
        end
      else
        case alter_identity do
          {:id, id} ->
            Utils.error_embed("You don't have an alter with ID **#{id}**.")

          {:alias, aliaz} ->
            Utils.error_embed("You don't have an alter with alias **#{aliaz}**.")
        end
      end
    end)
  end

  def remove_primary(%{system_identity: system_identity}, _options) do
    Accounts.set_primary_front(system_identity, nil)
    Utils.success_embed("Removed primary front.")
  end

  @impl true
  def type, do: :slash

  @impl true
  def options,
    do: [
      %{
        name: "view",
        description: "Views your currently fronting alters.",
        type: :sub_command
      },
      %{
        name: "add",
        description: "Adds an alter to front.",
        type: :sub_command,
        options: [
          %{
            name: "id",
            description: "The ID (or alias) of the alter to add to front.",
            type: :string,
            max_length: 80,
            required: true
          },
          %{
            name: "comment",
            description: "An optional comment to add to the front.",
            type: :string,
            max_length: 50,
            required: false
          },
          %{
            name: "set-primary",
            description: "Whether to set the alter as primary front.",
            type: :boolean,
            required: false
          }
        ]
      },
      %{
        name: "end",
        description: "Removes an alter from front.",
        type: :sub_command,
        options: [
          %{
            name: "id",
            description: "The ID (or alias) of the alter to end fronting.",
            type: :string,
            max_length: 80,
            required: true
          }
        ]
      },
      %{
        name: "set",
        description: "Sets an alter as front, replacing all other alters.",
        type: :sub_command,
        options: [
          %{
            name: "id",
            description: "The ID (or alias) of the alter to set to front.",
            type: :string,
            max_length: 80,
            required: true
          },
          %{
            name: "comment",
            description: "An optional comment to add to the front.",
            type: :string,
            max_length: 50,
            required: false
          },
          %{
            name: "set-primary",
            description: "Whether to set the alter as primary front.",
            type: :boolean,
            required: false
          }
        ]
      },
      %{
        name: "primary",
        description: "Sets the primary fronting alter.",
        type: :sub_command,
        options: [
          %{
            name: "id",
            description: "The ID (or alias) of the alter to set as primary front.",
            type: :string,
            max_length: 80,
            required: true
          },
          %{
            name: "add-to-front",
            description: "Whether to add the alter to front if they are not already fronting.",
            type: :boolean,
            required: false
          }
        ]
      },
      %{
        name: "remove-primary",
        description: "Removes the primary fronting alter.",
        type: :sub_command
      }
    ]
end
