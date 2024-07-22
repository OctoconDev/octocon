defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Alter.RemoveAlias do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/alter remove-alias`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/alter remove-alias` command removes an existing alias from an alter.
        ### Usage
        ```
        /alter remove-alias <id>
        ```
        ### Parameters
        - `id`: The ID (or alias) of the alter whose alias to delete. See the FAQ for more information about IDs and aliases.
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
