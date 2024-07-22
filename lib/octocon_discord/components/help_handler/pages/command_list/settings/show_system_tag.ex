defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Settings.ShowSystemTag do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/settings show-system-tag`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/settings show-system-tag` command toggles whether or not your system tag is shown when you proxy.

        **Note**: Servers can override this setting.
        ### Usage
        ```
        /settings show-system-tag
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
