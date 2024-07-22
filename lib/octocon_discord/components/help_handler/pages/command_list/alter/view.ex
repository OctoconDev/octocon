defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Alter.View do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/alter view`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/alter view` command views an alter's profile, including their description, avatar, and proxies.
        ### Usage
        ```
        /alter view <id>
        ```
        ### Parameters
        - `id`: The ID (or alias) of the alter whose profile to view. See the FAQ for more information about IDs and aliases.
        """
      }
    ]
  end

  def components(uid) do
    [
      %{
        type: 1,
        components: [
          back_button("alter_root", uid)
        ]
      }
    ]
  end
end
