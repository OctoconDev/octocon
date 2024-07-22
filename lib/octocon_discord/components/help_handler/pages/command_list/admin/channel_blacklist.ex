defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Admin.ChannelBlacklist do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.folder()} `/admin channel-blacklist`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/admin channel-blacklist` command group manages this server's channel blacklist. Channels (or entire categories) in this blacklist cannot be proxied in.
        ## #{Emojis.slashcommand()} `/admin channel-blacklist list`
        Views which channels/categories are in the channel blacklist.
        ### Usage
        ```
        /admin channel-blacklist list
        ```
        ## #{Emojis.slashcommand()} `/admin channel-blacklist add`
        Adds a channel or category to the channel blacklist.
        ### Usage
        ```
        /admin channel-blacklist add <channel>
        ```
        ### Parameters
        - `channel`: The channel or category to add to the channel blacklist.
        ## #{Emojis.slashcommand()} `/admin channel-blacklist remove`
        Removes a channel or category from the channel blacklist.
        ### Usage
        ```
        /admin channel-blacklist remove <channel>
        ```
        ### Parameters
        - `channel`: The channel or category to remove from the channel blacklist.
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
