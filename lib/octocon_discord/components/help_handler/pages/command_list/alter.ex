defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Alter do
  use OctoconDiscord.Components.HelpHandler.Pages

  @commands [
    %{
      name: "create",
      description: "Creates a new alter",
      nav_page: "alter_create"
    },
    %{
      name: "view",
      description: "Views an alter's profile",
      nav_page: "alter_view"
    },
    %{
      name: "edit",
      description: "Edits an alter's profile",
      nav_page: "alter_edit"
    },
    %{
      name: "delete",
      description: "Deletes an alter",
      nav_page: "alter_delete"
    },
    %{
      name: "list",
      description: "Lists all of your alters",
      nav_page: "alter_list"
    },
    %{
      name: "security",
      description: "Manages an alter's security level",
      nav_page: "alter_security"
    },
    %{
      name: "avatar",
      description: "Manages an alter's avatar (profile picture)",
      nav_page: "alter_avatar"
    },
    %{
      name: "proxy",
      description: "Manages an alter's proxies",
      nav_page: "alter_proxy"
    },
    %{
      name: "remove-alias",
      description: "Removes an alter's alias",
      nav_page: "alter_remove_alias"
    },
    %{
      name: "remove-proxy-name",
      description: "Removes an alter's proxy name",
      nav_page: "alter_remove_proxy_name"
    }
  ]

  def embeds do
    [
      %Embed{
        title: "#{Emojis.folder()} `/alter`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/alter` command group manages your alters. Choose a subcommand below to learn more about it!

        **Note**: Most alter commands require you to specify an alter. You can do this by their numerical ID **or** their *alias*. Aliases are unique alphanumeric IDs you can assign to your alters to make Discord commands easier to use.
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
