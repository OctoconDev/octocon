defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Help do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/help`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/help` command displays an interactive guide on how to use the Octocon bot. You're looking at it right now!
        ### Usage
        ```
        /help
        ```
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
