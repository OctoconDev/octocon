defmodule OctoconDiscord.Components.HelpHandler.Pages.Root do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "Welcome to the Octocon help interface!",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        This interactive guide will help you navigate the various features of the Octocon bot.

        What can I help you with today?
        ### #{Emojis.slashcommand()} Command list
        > View a list of all available `/slash commands` and how they're used.
        ### #{Emojis.faq()} FAQ
        > Get answers to frequently asked questions about Octocon.
        ### #{Emojis.resources()} Resources
        > Find links to the Octocon website, GitHub repository, and other useful resources.
        """
      }
    ]
  end

  def components(uid) do
    [
      %{
        type: 1,
        components: [
          %{
            type: 3,
            custom_id: "help|nav|#{uid}",
            options: [
              %{
                label: "Command list",
                value: "command_list",
                text: "View a list of all available `/slash commands` and how they're used.",
                emoji: map_emoji(Emojis.slashcommand())
              },
              %{
                label: "FAQ",
                value: "faq_root",
                text: "Get answers to frequently asked questions about Octocon.",
                emoji: map_emoji(Emojis.faq())
              },
              %{
                label: "Resources",
                value: "resources",
                text:
                  "Find links to the Octocon website, GitHub repository, and other useful resources.",
                emoji: map_emoji(Emojis.resources())
              }
            ]
          }
        ]
      }
    ]
  end
end
