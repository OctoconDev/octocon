defmodule OctoconDiscord.Components.HelpHandler.Pages.Faq.WhoCanSee do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.faq()} Who can see my alters/tags?",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        Unlike many alternatives, **all data is private by default** in Octocon. This is to ensure your privacy and security. No one can see your alters, tags, or other data unless you explicitly share it with them.
        ### Security levels
        If you'd like to share your alters/tags with someone, you can do so by editing their **security level**. There are four security levels in Octocon:

        - **Private**: No one but you can see this data (default).
        - **Trusted friends only**: Only **trusted** friends (see below) can see this data.
        - **Friends only**: All friends can see this data.
        - **Public**: Anyone can see this data, even if they're not your friend.

        When in doubt, please keep your alters and tags private! It's best practice to avoid sharing your data with people you don't trust, a concept that Octocon leans heavily into.
        ### Trusted friends
        Octocon models two levels of friendship: *normal* friends and *trusted* friends. If you'd like to mark a friend as a trusted friend, use the `/friend trust` command.
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
