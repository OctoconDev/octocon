defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Front.End do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/front end`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/front end` command ends an alter's current front.
        ### Usage
        ```
        /front end <id>
        ```
        ### Parameters
        - `id`: The ID (or alias) of the alter whose front to end. See the FAQ for more information about IDs and aliases.
        """
      }
    ]
  end

  def components(uid) do
    [
      %{
        type: 1,
        components: [
          back_button("front_root", uid)
        ]
      }
    ]
  end
end
