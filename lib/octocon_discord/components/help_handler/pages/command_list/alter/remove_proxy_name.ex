defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Alter.RemoveProxyName do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/alter remove-proxy-name`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/alter remove-proxy-name` command removes an existing proxy name from an alter.
        ### Usage
        ```
        /alter remove-proxy-name <id>
        ```
        ### Parameters
        - `id`: The ID (or alias) of the alter whose proxy name to delete. See the FAQ for more information about IDs and aliases.
        """
      }
    ]
  end

  def components(uid) do
    [
      %{
        type: 1,
        components: [
          back_button("alter_root", uid)
        ]
      }
    ]
  end
end
