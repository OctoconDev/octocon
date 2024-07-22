defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Alter.Proxy do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.folder()} `/alter proxy`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/alter proxy` command group manages an alter's chat proxies.

        Proxies allow you to send messages as an alter in a Discord channel. For example, if an alter has a proxy of `a-text`, typing `a-Hello, there!` will send `Hello, there!` as that alter in the current channel.
        ## #{Emojis.slashcommand()} `/alter proxy set`
        Removes all existing proxies from an alter, and sets a new one.
        ### Usage
        ```
        /alter proxy set <id> [prefix] [suffix]
        ```
        ### Parameters
        - `id`: The ID (or alias) of the alter whose proxy to set.
        - `prefix` (optional): The prefix to use for the proxy.
        - `suffix` (optional): The suffix to use for the proxy.
        ## #{Emojis.slashcommand()} `/alter proxy add`
        Adds a new proxy to an alter.
        ### Usage
        ```
        /alter proxy add <id> [prefix] [suffix]
        ```
        ### Parameters
        - `id`: The ID (or alias) of the alter whose proxy to add.
        - `prefix` (optional): The prefix to use for the proxy.
        - `suffix` (optional): The suffix to use for the proxy.
        ## #{Emojis.slashcommand()} `/alter proxy remove`
        Removes an existing proxy from an alter.
        ### Usage
        ```
        /alter proxy remove <id> [prefix] [suffix]
        ```
        ### Parameters
        - `id`: The ID (or alias) of the alter whose proxy to remove.
        - `prefix` (optional): The prefix of the proxy to remove.
        - `suffix` (optional): The suffix of the proxy to remove.
        ## #{Emojis.slashcommand()} `/alter proxy clear`
        Clears all existing proxies from an alter.
        ### Usage
        ```
        /alter proxy clear <id>
        ```
        ### Parameters
        - `id`: The ID (or alias) of the alter whose proxies to clear.
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
