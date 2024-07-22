defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Front do
  use OctoconDiscord.Components.HelpHandler.Pages

  @commands [
    %{
      name: "view",
      description: "Views your currently fronting alters",
      nav_page: "front_view"
    },
    %{
      name: "set",
      description: "Sets an alter as front (replacing others)",
      nav_page: "front_set"
    },
    %{
      name: "add",
      description: "Adds an alter to front",
      nav_page: "front_add"
    },
    %{
      name: "end",
      description: "Ends an alter's current front",
      nav_page: "front_end"
    },
    %{
      name: "primary",
      description: "Sets an alter as primary front",
      nav_page: "front_primary"
    },
    %{
      name: "remove_primary",
      description: "Removes an alter as primary front",
      nav_page: "front_remove_primary"
    }
  ]

  def embeds do
    [
      %Embed{
        title: "#{Emojis.folder()} `/front`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/front` command group manages your front status. Choose a subcommand below to learn more about it!

        **Note**: Most front commands require you to specify an alter. You can do this by their numerical ID **or** their *alias*. Aliases are unique alphanumeric IDs you can assign to your alters to make Discord commands easier to use.
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
