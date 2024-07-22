defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Settings.ImportSp do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/settings import-sp`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/settings import-sp` command imports existing alters from Simply Plural, including their names, descriptions, pronouns, and avatars.
        ### Usage
        ```
        /settings import-sp <token>
        ```
        ### Parameters
        - `token`: Your Simply Plural token. You can get this by opening the Simply Plural app and navigating to the following page:

        **Settings** -> **Account** -> **Tokens**

        From here:
        - Click the **Add Token** button
        - Enable the **Read** permission
        - Click the **Add Token** button at the bottom of the screen
        - Tap **Yes** 3 times to confirm
        - Click the copy button on the left of the token to copy it to your clipboard
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
