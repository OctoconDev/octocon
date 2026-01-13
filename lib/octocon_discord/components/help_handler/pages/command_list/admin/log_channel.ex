defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Admin.LogChannel do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.folder()} `/admin log-channel`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/admin log-channel` command group manages this server's log channel. Whenever a message is proxied, a log message detailing the message and its sender's Discord account is sent to this channel.
        ## #{Emojis.slashcommand()} `/admin log-channel set`
        Sets the log channel for this server.
        ### Usage
        ```
        /admin log-channel set <channel>
        ```
        ### Parameters
        - `channel`: The channel to set as the log channel.
        ## #{Emojis.slashcommand()} `/admin log-channel remove`
        Removes the log channel for this server.
        ### Usage
        ```
        /admin channel-blacklist remove
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
          back_button("admin_root", uid)
        ]
      }
    ]
  end
end
