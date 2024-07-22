defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Friend.Reject do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/friend reject`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/friend reject` command rejects an incoming friend request from another user.
        ### Usage
        ```
        /front reject [system-id | username]
        ```
        ### Parameters
        - `system-id`: The system ID (7-character lowercase string) of the user whose friend request to reject.
        - `username`: The username of the user whose friend request to reject.
        **Note**: You must provide either a system ID **or** a username!
        """
      }
    ]
  end

  def components(uid) do
    [
      %{
        type: 1,
        components: [
          back_button("friend_root", uid)
        ]
      }
    ]
  end
end
