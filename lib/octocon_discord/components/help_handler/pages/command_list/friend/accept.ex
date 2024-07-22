defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Friend.Accept do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/friend accept`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/friend accept` command accepts an incoming friend request from another user.
        ### Usage
        ```
        /front accept [system-id | username]
        ```
        ### Parameters
        - `system-id`: The system ID (7-character lowercase string) of the user whose friend request to accept.
        - `username`: The username of the user whose friend request to accept.
        - `discord`: The Discord ping of the user whose friend request to accept.
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
