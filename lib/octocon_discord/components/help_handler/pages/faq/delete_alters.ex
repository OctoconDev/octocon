defmodule OctoconDiscord.Components.HelpHandler.Pages.Faq.DeleteAlters do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.faq()} Can I delete all of my alters at once?",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        Yes! You can delete all of your alters at once by running the `/danger wipe-alters` command. Be careful, though; this action is **irreversible**!

        Once you run the command, check your DMs for a confirmation message.

        If you'd instead like to delete your entire account, you can run the `/danger delete-account` command.
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
