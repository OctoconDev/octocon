defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Settings.ProxyShowPronouns do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/settings proxy-show-pronouns`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/settings proxy-show-pronouns` command toggles whether or not an alter's pronouns are automatically shown as part of their display name when you proxy.

        This is useful to disable if you have alters with pronouns in their names, which is common after importing from PluralKit.
        ### Usage
        ```
        /settings proxy-show-pronouns
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
