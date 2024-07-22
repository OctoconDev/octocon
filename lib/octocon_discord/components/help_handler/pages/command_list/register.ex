defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Register do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/register`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/register` command creates a new Octocon account using your Discord account. You'll have to run this command before you can use most of Octocon's features!
        ### Usage
        ```
        /register
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
