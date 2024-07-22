defmodule OctoconDiscord.Components.HelpHandler.Pages.Faq.CommandsPrivate do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.faq()} Are my commands private?",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        Yes! Unlike many other proxy bots, Octocon uses **ephemeral** slash commands. This means that when you run a command, **only you** can see the response.

        This is a key feature of Octocon that ensures your privacy and security. You can use Octocon commands in public without worrying about other people seeing your data or spamming your friends!

        Additionally, all data in Octocon is private by default. No one can see your alters, tags, or other data unless you explicitly share it with them by editing their security level. See the corresponding FAQ entry for more information on security levels!
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
