defmodule OctoconDiscord.Components.HelpHandler.Pages.Resources do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.faq()} Resources",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        Here are some resources that you might find helpful:
        ### Official links
        - [Website](https://octocon.app)
        - [Support server](https://octocon.app/discord)
        - [GitHub organization](https://github.com/OctoconDev)
        - [Documentation](https://octocon.app/docs)
        ### DID/OSDD resources
        **Note**: The resources here are not affiliated with Octocon.

        - [did-research.org](https://did-research.org)

        > Please be wary of misinformation in the online system community, especially on social media platforms. Also, be wary of resources on platforms like Carrd.co; they are **very** likely to contain pseudoscience, unverified original research, and misinformation.
        """
      }
    ]
  end

  def components(uid) do
    [
      %{
        type: 1,
        components: [
          back_button("root", uid)
        ]
      }
    ]
  end
end
