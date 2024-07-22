defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Admin.ViewSettings do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/admin view-settings`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/admin view-settings` command views this server's current admin settings.
        ### Usage
        ```
        /admin view-settings
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
