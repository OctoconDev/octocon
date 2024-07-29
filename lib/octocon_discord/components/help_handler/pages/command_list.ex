defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList do
  use OctoconDiscord.Components.HelpHandler.Pages

  @commands [
    %{
      name: "help",
      description: "Interactive help guide",
      category: false,
      nav_page: "help"
    },
    %{
      name: "register",
      description: "Register a new account using Discord",
      category: false,
      nav_page: "register"
    },
    %{
      name: "settings",
      description: "Manage your settings and preferences",
      category: true,
      nav_page: "settings_root"
    },
    %{
      name: "alter",
      description: "Manage your alters and their profiles",
      category: true,
      nav_page: "alter_root"
    },
    %{
      name: "front",
      description: "Manage your front status",
      category: true,
      nav_page: "front_root"
    },
    %{
      name: "friend",
      description: "Manage your friends and friend requests",
      category: true,
      nav_page: "friend_root"
    },
    %{
      name: "autoproxy",
      description: "Manage your autoproxy settings",
      category: false,
      nav_page: "autoproxy"
    },
    %{
      name: "admin",
      description: "Tools for server administrators and moderators",
      category: true,
      nav_page: "admin_root"
    },
    %{
      name: "danger",
      description: "Dangerous commands that require extra confirmation",
      category: true,
      nav_page: "danger_root"
    },
    %{
      name: "bot-info",
      description: "Shows technical info about the Octocon bot",
      category: false,
      nav_page: "bot_info"
    }
  ]

  # # Joining this at compile-time avoids unnecessary work at runtime
  # @commands_joined @commands
  #                  |> Enum.map(fn %{name: name, description: description, category: category} ->
  #                    """
  #                    ### #{if category, do: "#{Emojis.folder()} ", else: ""}/#{name}
  #                    > #{description}
  #                    """
  #                  end)
  #                  |> Enum.join()

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} Command list",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        Octocon uses `/slash commands` to interact with the bot. Choose a command or category below to view more information!
        ### Note
        > All commands are **ephemeral**, meaning they only show up for you and are not visible to others.
        > 
        > You can use Octocon commands in public without worrying about other people seeing your data!

        #{Emojis.folder()} *= Category*
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
              |> Enum.map(fn %{
                               name: name,
                               description: description,
                               nav_page: nav_page,
                               category: category
                             } ->
                %{
                  label: "/" <> name,
                  value: nav_page,
                  description: description,
                  emoji: map_emoji(if category, do: Emojis.folder(), else: Emojis.slashcommand())
                }
              end)
          }
        ]
      },
      %{
        type: 1,
        components: [
          back_button("root", uid)
        ]
      }
    ]
  end
end
