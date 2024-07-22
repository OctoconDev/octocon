defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Front.RemovePrimary do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/front remove-primary`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/front remove-primary` command removes an alter from primary front, if one is currently set.
        ### Usage
        ```
        /front remove-primary
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
