defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Settings.Avatar do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.folder()} `/settings avatar`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/settings avatar` command group modifies your system's avatar (profile picture).
        ## #{Emojis.slashcommand()} `/settings avatar set`
        Sets an image as your new avatar.
        ### Usage
        ```
        /settings avatar set <avatar>
        ```
        ### Parameters
        - `avatar`: The image to set as your new avatar. Must be a PNG, JPEG, or WebP file.
        ## #{Emojis.slashcommand()} `/settings avatar remove`
        Removes your current avatar.
        ### Usage
        ```
        /settings avatar remove
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
          back_button("settings_root", uid)
        ]
      }
    ]
  end
end
