defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Alter.Delete do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/alter delete`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/alter delete` command deletes an existing alter.
        ### Usage
        ```
        /alter delete <id>
        ```
        ### Parameters
        - `id`: The ID (or alias) of the alter to delete. See the FAQ for more information about IDs and aliases.
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
