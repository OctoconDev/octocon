defmodule OctoconDiscord.Commands.Admin do
  @moduledoc false

  @behaviour Nosedrum.ApplicationCommand

  alias OctoconDiscord.ServerSettingsManager
  alias OctoconDiscord.ChannelBlacklistManager

  alias OctoconDiscord.Utils

  @subcommands %{
    "channel-blacklist" => &__MODULE__.channel_blacklist/2,
    "log-channel" => &__MODULE__.log_channel/2,
    "view-settings" => &__MODULE__.view_settings/2,
    "force-system-tags" => &__MODULE__.force_system_tags/2
  }

  @blacklist_subcommands %{
    "add" => &__MODULE__.channel_blacklist_add/2,
    "remove" => &__MODULE__.channel_blacklist_remove/2,
    "list" => &__MODULE__.channel_blacklist_list/2
  }

  @log_channel_subcommands %{
    "set" => &__MODULE__.log_channel_set/2,
    "remove" => &__MODULE__.log_channel_remove/2
  }

  @impl true
  def description, do: "Manages this server's settings."

  @impl true
  def command(interaction) do
    %{
      data: %{
        resolved: resolved,
        options: [
          %{
            name: name,
            options: options
          }
        ]
      },
      guild_id: guild_id,
      member: member,
      user: %{
        id: discord_id
      }
    } = interaction

    @subcommands[name].(
      %{resolved: resolved, discord_id: discord_id, guild_id: guild_id, member: member},
      options
    )
  end

  def channel_blacklist(context, options) do
    subcommand = hd(options)

    @blacklist_subcommands[subcommand.name].(
      context,
      subcommand.options
    )
  end

  def channel_blacklist_add(%{guild_id: guild_id} = context, options) do
    ensure_permissions(context, fn ->
      channel = Utils.get_command_option(options, "channel")

      case ChannelBlacklistManager.add(to_string(guild_id), to_string(channel)) do
        :ok ->
          Utils.success_embed("Added channel <##{channel}> to this server's proxy blacklist.")

        {:error, :already_blacklisted} ->
          Utils.error_embed("This channel is already blacklisted.")
      end
    end)
  end

  def channel_blacklist_remove(context, options) do
    ensure_permissions(context, fn ->
      channel = Utils.get_command_option(options, "channel")

      case ChannelBlacklistManager.remove(to_string(channel)) do
        :ok ->
          Utils.success_embed("Removed channel <##{channel}> from this server's proxy blacklist.")

        {:error, :not_blacklisted} ->
          Utils.error_embed("This channel is not currently blacklisted.")
      end
    end)
  end

  def channel_blacklist_list(%{guild_id: guild_id} = context, _options) do
    ensure_permissions(context, fn ->
      case ChannelBlacklistManager.get_all_for_guild(to_string(guild_id)) do
        [] ->
          Utils.error_embed("This server has no blacklisted channels.")

        channels ->
          [
            embeds: [
              %Nostrum.Struct.Embed{
                title: "Blacklisted Channels",
                description:
                  Enum.map_join(channels, "\n", fn channel ->
                    "- <##{channel.channel_id}>"
                  end)
              }
            ],
            ephemeral?: true
          ]
      end
    end)
  end

  def log_channel(context, options) do
    subcommand = hd(options)

    @log_channel_subcommands[subcommand.name].(
      context,
      subcommand.options
    )
  end

  def log_channel_set(%{guild_id: guild_id} = context, options, skip \\ false) do
    callback = fn ->
      channel = Utils.get_command_option(options, "channel")

      case ServerSettingsManager.edit_settings(to_string(guild_id), %{
             log_channel: to_string(channel)
           }) do
        :ok ->
          Utils.success_embed("Set <##{channel}> as this server's log channel.")

        {:error, :not_found} ->
          case ServerSettingsManager.create_settings(to_string(guild_id)) do
            :ok ->
              log_channel_set(context, options)

            _ ->
              Utils.error_embed("An error occurred while setting the log channel.")
          end

        _ ->
          Utils.error_embed("An error occurred while setting the log channel.")
      end
    end

    if skip do
      callback.()
    else
      ensure_permissions(context, callback)
    end
  end

  def log_channel_remove(%{guild_id: guild_id} = context, _options) do
    ensure_permissions(context, fn ->
      case ServerSettingsManager.edit_settings(to_string(guild_id), %{log_channel: nil}) do
        :ok ->
          Utils.success_embed("Removed the log channel for this server.")

        {:error, :not_found} ->
          Utils.error_embed("This server has no log channel set.")

        _ ->
          Utils.error_embed("An error occurred while removing the log channel.")
      end
    end)
  end

  def force_system_tags(%{guild_id: guild_id} = context, options) do
    ensure_permissions(context, fn ->
      case ServerSettingsManager.get_settings(to_string(guild_id)) do
        nil ->
          case ServerSettingsManager.create_settings(to_string(guild_id)) do
            :ok ->
              force_system_tags(context, options)

            _ ->
              Utils.error_embed("An error occurred while fetching this server's settings.")
          end

        settings ->
          new_value = not settings.force_system_tags

          case ServerSettingsManager.edit_settings(to_string(guild_id), %{
                 force_system_tags: new_value
               }) do
            :ok ->
              Utils.success_embed(
                "Toggled forcing system tags to **#{if(new_value, do: "on", else: "off")}**."
              )

            _ ->
              Utils.error_embed("An error occurred while toggling system tags.")
          end
      end
    end)
  end

  def view_settings(%{guild_id: guild_id} = context, options, skip \\ false) do
    callback = fn ->
      case ServerSettingsManager.get_settings(to_string(guild_id)) do
        nil ->
          case ServerSettingsManager.create_settings(to_string(guild_id)) do
            :ok ->
              view_settings(context, options, true)

            _ ->
              Utils.error_embed("An error occurred while fetching this server's settings.")
          end

        settings ->
          log_channel = settings.log_channel
          force_system_tags = settings.force_system_tags

          [
            embeds: [
              %Nostrum.Struct.Embed{
                title: "Server settings",
                fields: [
                  %Nostrum.Struct.Embed.Field{
                    name: "Force system tags?",
                    value: if(force_system_tags, do: "Yes", else: "No"),
                    inline: true
                  },
                  %Nostrum.Struct.Embed.Field{
                    name: "Log channel",
                    value:
                      case log_channel do
                        nil -> "None"
                        channel -> "<##{channel}>"
                      end,
                    inline: true
                  }
                ]
              }
            ],
            ephemeral?: true
          ]
      end
    end

    if skip do
      callback.()
    else
      ensure_permissions(context, callback)
    end
  end

  defp ensure_permissions(%{guild_id: guild_id, member: member}, callback) do
    guild = Utils.get_cached_guild(guild_id)
    permissions = Nostrum.Struct.Guild.Member.guild_permissions(member, guild)

    if Enum.member?(permissions, :manage_server) or Enum.member?(permissions, :administrator) do
      callback.()
    else
      Utils.error_embed("You don't have permission to do that.")
    end
  end

  @impl true
  def type, do: :slash

  @impl true
  def options,
    do: [
      %{
        name: "channel-blacklist",
        description: "Manages channel proxy blacklists.",
        type: :sub_command_group,
        options: [
          %{
            name: "add",
            description: "Adds a channel to this server's blacklist.",
            type: :sub_command,
            options: [
              %{
                name: "channel",
                type: :channel,
                description: "The channel to add.",
                required: true
              }
            ]
          },
          %{
            name: "remove",
            description: "Removes a channel from this server's blacklist.",
            type: :sub_command,
            options: [
              %{
                name: "channel",
                type: :channel,
                description: "The channel to remove.",
                required: true
              }
            ]
          },
          %{
            name: "list",
            description: "Lists all channels in this server's blacklist.",
            type: :sub_command
          }
        ]
      },
      %{
        name: "log-channel",
        description: "Manages the log channel for this server.",
        type: :sub_command_group,
        options: [
          %{
            name: "set",
            description: "Sets the log channel for this server.",
            type: :sub_command,
            options: [
              %{
                name: "channel",
                type: :channel,
                description: "The channel to set as the log channel.",
                required: true
              }
            ]
          },
          %{
            name: "remove",
            description: "Removes the log channel for this server.",
            type: :sub_command
          }
        ]
      },
      %{
        name: "force-system-tags",
        description: "Toggles whether system tags are forced on this server.",
        type: :sub_command
      },
      %{
        name: "view-settings",
        description: "Views this server's settings.",
        type: :sub_command
      }
    ]
end
