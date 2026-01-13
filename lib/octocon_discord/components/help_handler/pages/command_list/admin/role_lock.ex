defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Admin.RoleLock do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.folder()} `/admin role-lock`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/admin role-lock` command group manages this server's role lock. When set, only members with the specified role will be able to proxy messages
        ## #{Emojis.slashcommand()} `/admin role-lock set`
        Sets the role lock for this server.
        ### Usage
        ```
        /admin role-lock set <role>
        ```
        ### Parameters
        - `role`: The role to lock proxying to.
        ## #{Emojis.slashcommand()} `/admin role-lock remove`
        Removes the role lock for this server.
        ### Usage
        ```
        /admin role-lock remove
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
