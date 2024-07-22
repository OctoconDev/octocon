defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Friend.List do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/friend list`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/friend list` command views a list of all your friends.
        ### Usage
        ```
        /front list
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
