defmodule OctoconDiscord.Components.HelpHandler.Pages.Faq.AnotherQuestion do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.faq()} I have another question!",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        If your question isn't answered here, feel free to ask in our [support server](https://octocon.app/discord)! We're happy to help. :smile:

        If you're looking for help on a specific command, you can view the `Command list` section of this help interface for more information!
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
