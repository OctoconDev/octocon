defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Danger do
  use OctoconDiscord.Components.HelpHandler.Pages

  @commands [
    %{
      name: "wipe-alters",
      description: "Wipes all alters from your account",
      nav_page: "danger_wipe_alters"
    },
    %{
      name: "delete-account",
      description: "Deletes your Octocon account",
      nav_page: "danger_delete_account"
    }
  ]

  def embeds do
    [
      %Embed{
        title: "#{Emojis.folder()} `/danger`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/danger` command group contains **dangerous, irreversible** commands that require additional confirmation. Choose a subcommand below to learn more about it!
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
