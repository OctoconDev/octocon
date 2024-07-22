defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Settings.IdsAsAliases do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/settings ids-as-aliases`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/settings ids-as-aliases` command toggles whether or not alter IDs and aliases can be used as automatic proxy prefixes.

        When enabled, you can send a message with an alter's ID or alias followed by a dash (`-`) to proxy as that alter.

        For example, if you have an alter with the ID `1` and alias `Gaia`, sending `1-Hello, world!` or `Gaia-Hello, world!` will proxy "Hello, world!" as that alter.
        ### Usage
        ```
        /settings ids-as-aliases
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
