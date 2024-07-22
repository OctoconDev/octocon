defmodule OctoconDiscord.Components.HelpHandler.Pages.Faq.ImportAlters do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.faq()} Can I import my alters from PluralKit or Simply Plural?",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        Yep! You can import your alters from PluralKit or Simply Plural with the `/settings import-pk` and `/settings import-sp` commands, respectively.
        ### PluralKit
        To import your alters from PluralKit, you'll need your PluralKit "token" first. To receive this, DM the PluralKit bot with the command `pk;token`, then copy the long, random string it gives you.

        Then, run the `/settings import-pk token:your-token-here` command to import your alters. You should receive a DM soon after when the import is complete! Avatars (profile pictures) will take a little longer.
        ### Simply Plural
        To import your alters from Simply Plural, you'll need a "token" from Simply Plural first. You can get this by opening the Simply Plural app and navigating to the following page:

        **Settings** -> **Account** -> **Tokens**

        From here:
        - Click the **Add Token** button
        - Enable the **Read** permission
        - Click the **Add Token** button at the bottom of the screen
        - Tap **Yes** 3 times to confirm
        - Click the copy button on the left of the token to copy it to your clipboard

        Then, run the `/settings import-sp token:your-token-here` command to import your alters. You should receive a DM soon after when the import is complete! Avatars (profile pictures) will take a little longer.
        """
      }
    ]
  end

  def components(uid) do
    [
      %{
        type: 1,
        components: [
          back_button("faq_root", uid)
        ]
      }
    ]
  end
end
