defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Admin do
  use OctoconDiscord.Components.HelpHandler.Pages

  @commands [
    %{
      name: "view-settings",
      description: "Views the server's admin settings",
      nav_page: "admin_view_settings"
    },
    %{
      name: "channel-blacklist",
      description: "Manages channels that can't be proxied in",
      nav_page: "admin_channel_blacklist"
    },
    %{
      name: "log-channel",
      description: "Manages the proxy log channel for this server",
      nav_page: "admin_log_channel"
    },
    %{
      name: "force-system-tags",
      description: "Toggles whether system tags are forced on this server",
      nav_page: "admin_force_system_tags"
    }
  ]

  def embeds do
    [
      %Embed{
        title: "#{Emojis.folder()} `/admin`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/admin` command group contains utilities for server administrators. Choose a subcommand below to learn more about it!
        """
      }
    ]
  end

  def components(uid) do
    [
      %{
        type: 1,
        components: [
          %{
            type: 3,
            custom_id: "help|nav|#{uid}",
            options:
              @commands
              |> Enum.map(fn %{name: name, description: description, nav_page: nav_page} ->
                %{
                  label: "/" <> name,
                  value: nav_page,
                  description: description,
                  emoji: map_emoji(Emojis.slashcommand())
                }
              end)
          }
        ]
      },
      %{
        type: 1,
        components: [
          back_button("command_list", uid)
        ]
      }
    ]
  end
end
