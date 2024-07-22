defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Alter.Avatar do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.folder()} `/alter avatar`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/alter avatar` command group modifies an alter's avatar (profile picture).
        ## #{Emojis.slashcommand()} `/alter avatar set`
        Sets an image as an alter's new avatar.
        ### Usage
        ```
        /alter avatar set <id> <avatar>
        ```
        ### Parameters
        - `id`: The ID (or alias) of the alter whose avatar to set.
        - `avatar`: The image to set as the alter's new avatar. Must be a PNG, JPEG, or WebP file.
        ## #{Emojis.slashcommand()} `/settings avatar remove`
        Removes an alter's current avatar.
        ### Usage
        ```
        /alter avatar remove <id>
        ```
        ### Parameters
        - `id`: The ID (or alias) of the alter whose avatar to remove.
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
