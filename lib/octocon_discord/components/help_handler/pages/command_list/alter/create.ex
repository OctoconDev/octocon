defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Alter.Create do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/alter create`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/alter create` command creates a new alter.
        ### Usage
        ```
        /alter create <name>
        ```
        ### Parameters
        - `name`: The name of the new alter. Must be below 80 characters.
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
