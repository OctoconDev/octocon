defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Friend do
  use OctoconDiscord.Components.HelpHandler.Pages

  @commands [
    %{
      name: "list",
      description: "Lists all your friends",
      nav_page: "friend_list"
    },
    %{
      name: "list-requests",
      description: "Lists all your friend requests",
      nav_page: "friend_list_requests"
    },
    %{
      name: "add",
      description: "Sends a friend request to another user",
      nav_page: "friend_add"
    },
    %{
      name: "remove",
      description: "Removes a friend from your friends list",
      nav_page: "friend_remove"
    },
    %{
      name: "accept",
      description: "Accepts a friend request",
      nav_page: "friend_accept"
    },
    %{
      name: "reject",
      description: "Rejects a friend request",
      nav_page: "friend_reject"
    },
    %{
      name: "cancel",
      description: "Cancels an outgoing friend request",
      nav_page: "friend_cancel"
    },
    %{
      name: "trust",
      description: "Trusts a friend",
      nav_page: "friend_trust"
    },
    %{
      name: "untrust",
      description: "Untrusts a friend",
      nav_page: "friend_untrust"
    }
  ]

  def embeds do
    [
      %Embed{
        title: "#{Emojis.folder()} `/friend`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/friend` command group manages your friends and friend requests. Choose a subcommand below to learn more about it!
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
