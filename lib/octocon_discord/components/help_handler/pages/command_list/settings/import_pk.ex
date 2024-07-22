defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Settings.ImportPk do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/settings import-pk`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/settings import-pk` command imports existing alters from PluralKit, including their names, descriptions, pronouns, and avatars.

        If an alter has a display name set in PluralKit, it will also be imported as a **proxy name** in Octocon.
        ### Usage
        ```
        /settings import-pk <token>
        ```
        ### Parameters
        - `token`: Your PluralKit token. You can get this by DMing PluralKit with the `pk;token` command and copying the random string it gives you.
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
