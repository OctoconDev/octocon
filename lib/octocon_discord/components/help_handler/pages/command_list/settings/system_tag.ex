defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Settings.SystemTag do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/settings system-tag`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/settings system-tag` command changes your system's system tag. System tags are short bits of text that can be added to the end of a proxied alter's name to identify you.

        **Note**: System tags aren't shown by default! Use the `/settings show-system-tag` command to enable them. Servers can override this setting.

        Your new system tag must satisfy the following criteria:
        - Between 1-20 characters.
        ### Usage
        ```
        /settings system-tag <tag>
        ```
        ### Parameters
        - `tag`: The new system tag to set.
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
