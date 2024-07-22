defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Danger.DeleteAccount do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/danger delete-account`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/danger delete-account` command **irreversibly** deletes the Octocon account associated with your Discord account. Upon running it, you will receive a DM from the Octocon bot with a confirmation prompt.

        This will also reset your account's alter count, so the next alter you create will have the ID `1`.
        ### Usage
        ```
        /danger delete-account
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
          back_button("danger_root", uid)
        ]
      }
    ]
  end
end
