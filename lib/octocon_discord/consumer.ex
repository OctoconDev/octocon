defmodule OctoconDiscord.Consumer do
  @moduledoc false

  use Nostrum.Consumer
  require Logger

  alias Nostrum.ConsumerGroup

  alias OctoconDiscord.{
    Commands,
    Components
  }

  alias OctoconDiscord.Events.MessageCreate

  @commands %{
    "register" => Commands.Register,
    "help" => Commands.Help,
    "system" => Commands.System,
    "settings" => Commands.Settings,
    "alter" => Commands.Alter,
    "autoproxy" => Commands.Autoproxy,
    "global-autoproxy" => Commands.GlobalAutoproxy,
    "danger" => Commands.Danger,
    "bot-info" => Commands.BotInfo,
    "friend" => Commands.Friend,
    "admin" => Commands.Admin,
    "front" => Commands.Front,
    # "octo" => Commands.Octo,
    "❓ Who is this?" => Commands.Messages.WhoIsThis,
    "🔔 Ping account" => Commands.Messages.PingAccount,
    "❌ Delete proxied message" => Commands.Messages.DeleteProxiedMessage,
    "🔄 Reproxy message" => Commands.Messages.Reproxy
  }

  @impl GenServer
  def init([]) do
    Logger.info("OctoconDiscord.Consumer init")
    ConsumerGroup.join(self())
    {:ok, nil}
  end

  def handle_event({:READY, %{shard: {this_shard, shard_count}}, _ws_state}) do
    if this_shard == 0 do
      Logger.info(
        "First shard is READY; bulk-registering all application commands (#{map_size(@commands)})..."
      )

      scope = Application.get_env(:octocon, :nostrum_scope)

      Enum.each(@commands, fn {name, module} ->
        Nosedrum.Storage.Dispatcher.queue_command(name, module)
      end)

      Logger.info("Registering commands for scope: #{inspect(scope)}")
      case Nosedrum.Storage.Dispatcher.process_queue(scope) do
        {:ok, _} ->
          Logger.info("Registered all commands!")

        {:error, e} ->
          Logger.error("Failed to register all commands: #{inspect(e)}")
      end
    end

    if this_shard == shard_count - 1 do
      Logger.info("Last shard is READY; starting OctoconDiscord.StatusUpdater.")
      OctoconDiscord.StatusUpdater.start()
    end

    :ok
  end

  def handle_event({:INTERACTION_CREATE, interaction, _ws_state}) do
    if interaction.type in [3, 5] do
      Components.dispatch(interaction)
    else
      Nosedrum.Storage.Dispatcher.handle_interaction(interaction)
    end
  rescue
    e ->
      Nostrum.Api.create_interaction_response(interaction, %{
        type: :integer,
        data: %{
          flags: 64,
          embeds: [
            %{
              title: ":x: Whoops!",
              description: "An error occurred while processing your command.",
              color: 0xFF0000
            }
          ]
        }
      })

      reraise e, __STACKTRACE__
  end

  def handle_event({:MESSAGE_CREATE, data, _ws_state}) do
    MessageCreate.handle(data)
  end

  # def handle_event({:MESSAGE_DELETE, data, _ws_state}) do
  #  MessageDelete.handle(data)
  # end

  # def handle_event({:MESSAGE_UPDATE, data, _ws_state}) do
  # MessageUpdate.handle(data)
  # end

  # def handle_event({:MESSAGE_REACTION_ADD, data, _ws_state}) do
  #  MessageReactionAdd.handle(data)
  # end

  def handle_event(_data) do
  end
end
