defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Autoproxy do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/autoproxy`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/autoproxy` command changes your autoproxy mode. There are three modes:

        - **None**: No messages will be autoproxied (default).
        - **Latch**: Messages will be autoproxied as the *alter who proxied last*.
        - **Front**: Messages will be autoproxied as the *alter set as primary front*. If no primary front is set, messages will be proxied as the *current longest-fronting alter*.
        ### Usage
        ```
        /autoproxy <mode>
        ```
        ### Parameters
        - `mode`: The autoproxy mode to set. Must be one of `none`, `latch`,  or `front`.
        """
      }
    ]
  end

  def components(uid) do
    [
      %{
        type: 1,
        components: [
          back_button("command_list", uid)
        ]
      }
    ]
  end
end
