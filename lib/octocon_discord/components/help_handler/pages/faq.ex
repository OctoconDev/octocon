defmodule OctoconDiscord.Components.HelpHandler.Pages.Faq do
  use OctoconDiscord.Components.HelpHandler.Pages

  @questions [
    %{
      name: "How do I get started with the bot?",
      emoji: Emojis.one(),
      nav_page: "faq_get_started"
    },
    %{
      name: "Can I import my alters from PluralKit or Simply Plural?",
      emoji: Emojis.two(),
      nav_page: "faq_import_alters"
    },
    %{
      name: "Are my commands private?",
      emoji: Emojis.three(),
      nav_page: "faq_commands_private"
    },
    %{
      name: "How do I invite Octocon to my server?",
      emoji: Emojis.four(),
      nav_page: "faq_invite_octocon"
    },
    %{
      name: "Who can see my alters/tags?",
      emoji: Emojis.five(),
      nav_page: "faq_who_can_see"
    },
    %{
      name: "How do IDs and aliases work?",
      emoji: Emojis.six(),
      nav_page: "faq_ids_aliases"
    },
    %{
      name: "How does autoproxy work?",
      emoji: Emojis.seven(),
      nav_page: "faq_autoproxy"
    },
    %{
      name: "Can I delete all of my alters at once?",
      emoji: Emojis.eight(),
      nav_page: "faq_delete_alters"
    },
    %{
      name: "I have another question!",
      emoji: Emojis.nine(),
      nav_page: "faq_another_question"
    }
  ]

  # Joining this at compile-time avoids unnecessary work at runtime
  @questions_joined @questions
                    |> Enum.map(fn %{name: name, emoji: emoji} ->
                      "### #{emoji} #{name}"
                    end)
                    |> Enum.join("\n")

  def embeds do
    [
      %Embed{
        title: "#{Emojis.faq()} FAQ",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        Have a question about how to use Octocon? Check out the FAQ below! Use the dropdown menu to navigate to a specific question.
        #{@questions_joined}
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
            options:
              @questions
              |> Enum.map(fn %{
                               name: name,
                               emoji: emoji,
                               nav_page: nav_page
                             } ->
                %{
                  label: name,
                  value: nav_page,
                  emoji: map_emoji(emoji)
                }
              end)
          }
        ]
      },
      %{
        type: 1,
        components: [
          back_button("root", uid)
        ]
      }
    ]
  end
end
