defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Settings.ProxyCaseSensitivity do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/settings proxy-case-sensitivity`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/settings proxy-case-sensitivity` command toggles whether or not your alter's proxies are case-sensitive.

        For example, if you have an alter with the proxy `a-text`, and you have this setting enabled, that alter can also proxy as `A-text`.
        ### Usage
        ```
        /settings proxy-case-sensitivity
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
