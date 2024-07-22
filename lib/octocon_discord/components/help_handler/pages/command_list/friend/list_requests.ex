defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Friend.ListRequests do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/friend list-requests`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/friend list-requests` command views a list of all your incoming and outgoing friend requests.
        ### Usage
        ```
        /front list-requests
        ```
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
