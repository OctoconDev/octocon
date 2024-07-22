defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Settings.Username do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/settings username`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/settings username` command changes your system's username. Usernames can be used to identify your system to other users in place of your system ID.

        Your new username must satisfy the following criteria:
        - Between 5-16 characters
        - Only contains letters, numbers, dashes, and underscores
        - Does not start or end with a symbol
        - Does not consist of seven lowercase letters in a row (like a system ID)
        ### Usage
        ```
        /settings username <username>
        ```
        ### Parameters
        - `username`: The new username to set.
        """
      }
    ]
  end

  def components(uid) do
    [
      %{
        type: 1,
        components: [
          back_button("settings_root", uid)
        ]
      }
    ]
  end
end
