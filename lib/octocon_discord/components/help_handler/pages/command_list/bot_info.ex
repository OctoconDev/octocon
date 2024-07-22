defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.BotInfo do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/bot-info`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/bot-info` command shows technical information and metrics about the Octocon bot.

        - **"Node count"** is the number of nodes (servers) in the Octocon bot's cluster.
        - **"Shard count"** is the number of shards the bot is using. Discord distributes servers across shards to reduce load.
        - **Guilds** are Discord's internal name for servers.
        ### Usage
        ```
        /bot-info
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
          back_button("command_list", uid)
        ]
      }
    ]
  end
end
