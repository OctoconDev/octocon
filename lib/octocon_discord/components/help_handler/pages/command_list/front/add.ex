defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Front.Add do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/front add`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/front add` command adds an alter to front.
        ### Usage
        ```
        /front add <id> [comment] [set-primary]
        ```
        ### Parameters
        - `id`: The ID (or alias) of the alter to add to front. See the FAQ for more information about IDs and aliases.
        - `comment`: **Optional**. A comment to add to this front.
        - `set-primary`: **Optional**. If present, this alter will also be set as primary front.
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
