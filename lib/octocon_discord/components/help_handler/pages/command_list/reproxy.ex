defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Reproxy do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/reproxy`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/reproxy` command edits your last-proxied message to be sent as a different alter, optionally with different content.
        ### Usage
        ```
        /reproxy <id> [text]
        ```
        ### Parameters
        - `id`: The ID (or alias) of the alter whose message to reproxy. See the FAQ for more information about IDs and aliases.
        - `text`: **Optional**. The new content to send. If not provided, the content will be unmodified.
        """
      }
    ]
  end

  def components(uid) do
    [
      %{
        type: 1,
        components: [
          back_button("command_list", uid)
        ]
      }
    ]
  end
end
