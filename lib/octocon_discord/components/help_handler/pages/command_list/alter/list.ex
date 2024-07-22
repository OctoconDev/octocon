defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Alter.List do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/alter list`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/alter list` command views of a list of your alters.

        If you have more than 20 alters, they will be paginated. You can use the buttons below the list to navigate between pages.
        ### Usage
        ```
        /alter list [sort]
        ```
        ### Parameters
        - `sort` (optional): The sorting method to use for the list. Can be one of the following:
          - `ID`: Sort by ID (default)
          - `Alphabetical`: Sort alphabetically by name
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
