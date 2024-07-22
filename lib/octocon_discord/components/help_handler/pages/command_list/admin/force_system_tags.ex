defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Admin.ForceSystemTags do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/admin force-system-tags`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/admin force-system-tags` command toggles whether system tags are forced to be used on this server when proxying. This **overrides** every user's personal setting!
        ### Usage
        ```
        /admin force-system-tags
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
          back_button("admin_root", uid)
        ]
      }
    ]
  end
end
