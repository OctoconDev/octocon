defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Friend.Cancel do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/friend cancel`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/friend cancel` command cancels an outgoing friend request to another user.
        ### Usage
        ```
        /front cancel [system-id | username]
        ```
        ### Parameters
        - `system-id`: The system ID (7-character lowercase string) of the user whose friend request to cancel.
        - `username`: The username of the user whose friend request to cancel.
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
