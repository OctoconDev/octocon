defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Friend.Add do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/friend add`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/friend add` command sends a friend request to another user.
        ### Usage
        ```
        /front add [system-id | username]
        ```
        ### Parameters
        - `system-id`: The system ID (7-character lowercase string) of the user to send a friend request to.
        - `username`: The username of the user to send a friend request to.
        - `discord`: The Discord ping of the user to send a friend request to.
        **Note**: You must provide exactly **one** of the above parameters!
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
