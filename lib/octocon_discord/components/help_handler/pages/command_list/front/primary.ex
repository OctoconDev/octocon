defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Front.Primary do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/front primary`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/front primary` command sets a currently fronting alter as primary front. This will make this alter appear at the top of the front list, and will proxy as them by default when `/autoproxy` is set to `Front` mode.
        ### Usage
        ```
        /front primary <id>
        ```
        ### Parameters
        - `id`: The ID (or alias) of the alter to set as primary front. See the FAQ for more information about IDs and aliases.
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
