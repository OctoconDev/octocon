defmodule OctoconDiscord.Components.HelpHandler.Pages.Faq.GetStarted do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.faq()} How do I get started with the bot?",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        First, you'll have to create an account! You can do this with the `/register` command.

        After you've registered, you can either import alters from PluralKit or Simply Plural (see the respective FAQ entry for more details), or create alters from scratch with the `/alter create` command.

        If you're ever confused on how to use a command, you can view the `Command list` section of this help interface for more information!
        ### Quick start
        - `/register` - Create a new account
        - `/alter create name:Gaia` - Create an alter with the name "Gaia"
        - `/alter edit id:1 alias:Gaia pronouns:they/he` - Edit the new alter's alias and pronouns
        - `/alter view id:Gaia` - View the new alter's information (using the alias you just set)
        - `/front add id:Gaia` - Add the new alter to front
        - `/alter proxy add id:Gaia prefix:g-` - Add a proxy tag to the new alter
        - `g-Hello, there!` - Proxy as the alter with the proxy you just created
        ### We're here to help!
        If you have any questions, feel free to join our [support server](https://octocon.app/discord)! We're happy to help. :smile:
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
