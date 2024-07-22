defmodule OctoconDiscord.Commands.BotInfo do
  @moduledoc false

  @behaviour Nosedrum.ApplicationCommand

  alias OctoconDiscord.Utils

  alias Octocon.{
    Accounts,
    Alters
  }

  @impl true
  def description, do: "Views the Octocon bot's metrics."

  @impl true
  def command(_interaction) do
    process_count = :erlang.system_info(:process_count)
    process_limit = :erlang.system_info(:process_limit)
    process_percentage = (process_count / process_limit) |> Kernel.*(100) |> Float.ceil(2)

    beam_uptime =
      ((:erlang.statistics(:wall_clock) |> elem(0)) / 1000)
      |> floor()
      |> Timex.Duration.from_seconds()
      |> Timex.format_duration(:humanized)

    # [_, {_, _, scheduler_utilization} | _rest] = :scheduler.utilization(2)

    memory_data = :memsup.get_system_memory_data()

    # System.schedulers() is a crude way of measuring the number of cores on the machine
    d = System.schedulers() * 256

    load_to_percent = fn load ->
      100 * (1 - d / (d + load))
    end

    cpu_usage =
      "#{:cpu_sup.avg1() |> load_to_percent.() |> Float.ceil(2)}% / #{:cpu_sup.avg5() |> load_to_percent.() |> Float.ceil(2)}% / #{:cpu_sup.avg15() |> load_to_percent.() |> Float.ceil(2)}%"

    total_memory =
      Keyword.get(memory_data, :total_memory)
      |> Kernel./(1024 * 1024)
      |> Float.ceil(2)

    available_memory =
      Keyword.get(memory_data, :available_memory)
      |> Kernel./(1024 * 1024)
      |> Float.ceil(2)

    used_memory = (total_memory - available_memory) |> Float.ceil(2)

    guild_count = Nostrum.Cache.GuildCache.fold(0, fn _, acc -> acc + 1 end)

    [
      ephemeral?: false,
      embeds: [
        %Nostrum.Struct.Embed{
          title: "Octocon Bot Info",
          color: Utils.hex_to_int("#0FBEAA"),
          fields: [
            %Nostrum.Struct.Embed.Field{
              name: "Local node information",
              value: " ",
              inline: false
            },
            %Nostrum.Struct.Embed.Field{
              name: "Processes",
              value: "#{process_count} / #{process_limit}\n(#{process_percentage}%)",
              inline: true
            },
            %Nostrum.Struct.Embed.Field{
              name: "CPU (1/5/15 mins.)",
              value: "#{cpu_usage}",
              inline: true
            },
            %Nostrum.Struct.Embed.Field{
              name: "Memory",
              value:
                "#{used_memory} MB / #{total_memory} MB\n(#{Float.ceil(used_memory / total_memory * 100, 2)}%)",
              inline: true
            },
            %Nostrum.Struct.Embed.Field{
              name: "Uptime",
              value: beam_uptime,
              inline: true
            },
            %Nostrum.Struct.Embed.Field{
              name: "Global information",
              value: " ",
              inline: false
            },
            %Nostrum.Struct.Embed.Field{
              name: "Shard count",
              value: "#{Nostrum.Util.gateway() |> elem(1)}",
              inline: true
            },
            %Nostrum.Struct.Embed.Field{
              name: "Node count",
              value: "#{Enum.count(Node.list()) + 1}",
              inline: true
            },
            %Nostrum.Struct.Embed.Field{
              name: " ",
              value: " ",
              inline: false
            },
            %Nostrum.Struct.Embed.Field{
              name: "Guilds",
              value: "#{guild_count}",
              inline: true
            },
            %Nostrum.Struct.Embed.Field{
              name: "Systems",
              value: "#{Accounts.count()}",
              inline: true
            },
            %Nostrum.Struct.Embed.Field{
              name: "Alters",
              value: "#{Alters.count()}",
              inline: true
            }
          ]
        }
      ]
    ]
  end

  @impl true
  def type, do: :slash

  @impl true
  def options, do: []
end
