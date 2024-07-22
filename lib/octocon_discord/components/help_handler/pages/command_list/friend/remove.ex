defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Friend.Remove do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/friend remove`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/friend remove` command removes a friend from your friends list.
        ### Usage
        ```
        /front remove [system-id | username]
        ```
        ### Parameters
        - `system-id`: The system ID (7-character lowercase string) of the friend to remove.
        - `username`: The username of the friend to remove.
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
