defmodule OctoconDiscord.Commands.System do
  @moduledoc false

  @behaviour Nosedrum.ApplicationCommand

  alias Octocon.{
    Accounts,
    Alters,
    Fronts
  }

  alias OctoconDiscord.Utils

  @subcommands %{
    "me" => &__MODULE__.me/2,
    "view" => &__MODULE__.view/2,
    "alter" => &__MODULE__.alter/2,
    "fronting" => &__MODULE__.fronting/2
  }

  @impl true
  def description, do: "Views information about your or another user's system."

  @impl true
  def command(interaction) do
    %{data: %{resolved: resolved, options: [%{name: name, options: options}]}, user: %{id: discord_id}} = interaction
    discord_id = to_string(discord_id)

    cb = fn -> @subcommands[name].(%{resolved: resolved, discord_id: discord_id}, options) end

    case name do
      "view" -> cb.()
      _ -> Utils.ensure_registered(discord_id, cb)
    end
  end

  def me(%{discord_id: discord_id}, _options) do
    system = Accounts.get_user!({:discord, discord_id})
    Utils.system_embed(system, true)
  end

  def view(_context, options) do
    opts = %{
      system_id: Utils.get_command_option(options, "system-id"),
      discord_id: Utils.get_command_option(options, "discord"),
      username: Utils.get_command_option(options, "username")
    }

    Utils.system_id_from_opts(opts, fn identity, _ ->
      system = Accounts.get_user!(identity)
      Utils.system_embed(system, false)
    end)
  end

  def alter(%{discord_id: discord_id}, options) do
    opts = %{
      system_id: Utils.get_command_option(options, "system-id"),
      discord_id: Utils.get_command_option(options, "discord"),
      username: Utils.get_command_option(options, "username")
    }

    alter_id = Utils.get_command_option(options, "alter-id")

    Utils.system_id_from_opts(opts, fn identity, _ ->
      if Utils.alter_id_valid?(alter_id) do
        case Alters.get_alter_guarded(identity, {:id, alter_id}, {:discord, discord_id}) do
          :error ->
            Utils.error_embed(
              "Could not access this alter. You may not have permission to view them."
            )

          {:ok, alter} ->
            [
              content:
                if(alter.security_level !== :public,
                  do:
                    "**NOTE:** This alter's information is only visible to you. You probably shouldn't share this with anyone else.",
                  else: nil
                ),
              embeds: [Utils.alter_embed(alter, true)],
              ephemeral?: true
            ]
        end
      else
        Utils.error_embed("**#{alter_id}** is not a valid alter ID.")
      end
    end)
  end

  def fronting(%{discord_id: discord_id}, options) do
    opts = %{
      system_id: Utils.get_command_option(options, "system-id"),
      discord_id: Utils.get_command_option(options, "discord"),
      username: Utils.get_command_option(options, "username")
    }

    Utils.system_id_from_opts(opts, fn identity, decorator ->
      currently_fronting =
        Fronts.currently_fronting_guarded(identity, {:discord, discord_id})

      if currently_fronting == [] do
        Utils.error_embed(
          "No alters are currently fronting in that system, or you do not have permission to view them."
        )
      else
        now_epoch = Timex.Duration.now(:second)

        [
          embeds: [
            %Nostrum.Struct.Embed{
              title:
                "Currently fronting alters for system #{decorator} (#{length(currently_fronting)})",
              description:
                Enum.map_join(
                  currently_fronting,
                  "\n",
                  fn %{front: front, alter: alter, primary: primary} ->
                    start_epoch = front.time_start |> Timex.to_unix()

                    "- `#{alter.id}  ` **#{alter.name}** #{case front.comment do
                      [] -> ""
                      "" -> ""
                      comment -> "(#{comment})"
                    end}#{if primary,
                      do: " :star:",
                      else: ""}\n  - *#{(now_epoch - start_epoch) |> Timex.Duration.from_seconds() |> Timex.format_duration(:humanized)}*\n"
                  end
                ),
              footer: %Nostrum.Struct.Embed.Footer{
                text: "⭐ = Primary front"
              }
            }
          ],
          ephemeral?: true
        ]
      end
    end)
  end

  @impl true
  def type, do: :slash

  @impl true
  def options,
    do: [
      %{
        name: "me",
        description: "Views information about your system.",
        type: :sub_command
      },
      %{
        name: "view",
        description: "Views information about another user's system.",
        type: :sub_command,
        options: [
          %{
            name: "system-id",
            description: "The ID of the system to view.",
            type: :string,
            min_length: 7,
            max_length: 7,
            required: false
          },
          %{
            name: "discord",
            description: "The Discord ping of the user to view.",
            type: :user,
            required: false
          },
          %{
            name: "username",
            description: "The username of the user to view.",
            type: :string,
            min_length: 5,
            max_length: 16,
            required: false
          }
        ]
      },
      %{
        name: "alter",
        description: "Views information about another system's alter.",
        type: :sub_command,
        options: [
          %{
            name: "alter-id",
            description: "The ID of the alter to view.",
            type: :integer,
            required: true
          },
          %{
            name: "system-id",
            description: "The ID of the system whose alter to view.",
            type: :string,
            min_length: 7,
            max_length: 7,
            required: false
          },
          %{
            name: "discord",
            description: "The Discord ping of the user whose alter to view.",
            type: :user,
            required: false
          },
          %{
            name: "username",
            description: "The username of the user whose alter to view.",
            type: :string,
            min_length: 5,
            max_length: 16,
            required: false
          }
        ]
      },
      %{
        name: "fronting",
        description: "Views the currently fronting alters of another system.",
        type: :sub_command,
        options: [
          %{
            name: "system-id",
            description: "The ID of the system whose fronting alters to view.",
            type: :string,
            min_length: 7,
            max_length: 7,
            required: false
          },
          %{
            name: "discord",
            description: "The Discord ping of the user whose fronting alters to view.",
            type: :user,
            required: false
          },
          %{
            name: "username",
            description: "The username of the user whose fronting alters to view.",
            type: :string,
            min_length: 5,
            max_length: 16,
            required: false
          }
        ]
      }
    ]
end
