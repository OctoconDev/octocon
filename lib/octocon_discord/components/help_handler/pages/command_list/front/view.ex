defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Front.View do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/front view`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/front view` command views a list of alters that are currently fronting.
        ### Usage
        ```
        /front view
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
          back_button("front_root", uid)
        ]
      }
    ]
  end
end
