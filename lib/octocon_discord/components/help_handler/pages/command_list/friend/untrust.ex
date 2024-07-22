defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Friend.Untrust do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/friend untrust`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/friend untrust` command removes a friend's "trusted friend" status.

        Trusted friends are able to view all alters, tags, etc. that you have set to "Trusted friends only." **Friends do not know whether they are trusted or not!**
        ### Usage
        ```
        /front untrust [system-id | username]
        ```
        ### Parameters
        - `system-id`: The system ID (7-character lowercase string) of the friend to untrust.
        - `username`: The username of the friend to untrust.
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
