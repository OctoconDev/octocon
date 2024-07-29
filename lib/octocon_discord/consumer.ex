defmodule OctoconDiscord.Consumer do
  use Nostrum.Consumer
  require Logger

  alias Nostrum.ConsumerGroup
  alias OctoconDiscord.Components
  alias OctoconDiscord.Commands

  alias OctoconDiscord.Events.{
    MessageCreate
  }

  @commands %{
    "register" => Commands.Register,
    "help" => Commands.Help,
    "system" => Commands.System,
    "settings" => Commands.Settings,
    "alter" => Commands.Alter,
    "autoproxy" => Commands.Autoproxy,
    "danger" => Commands.Danger,
    "bot-info" => Commands.BotInfo,
    "friend" => Commands.Friend,
    "admin" => Commands.Admin,
    "front" => Commands.Front,
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

  def handle_event({:READY, _data, _ws_state}) do
    # Using :persistent_term here ensures we only do this once (when shard 0 inits)
    unless :persistent_term.get({OctoconDiscord, :commands_registered}, false) do
      Logger.info("Bulk-registering all application commands (#{map_size(@commands)})...")

      scope = Application.get_env(:octocon, :nostrum_scope)

      Enum.each(@commands, fn {name, module} ->
        Nosedrum.Storage.Dispatcher.queue_command(name, module)
      end)

      case Nosedrum.Storage.Dispatcher.process_queue(scope) do
        {:ok, _} ->
          Logger.info("Registered all commands!")
          :persistent_term.put({OctoconDiscord, :commands_registered}, true)

        {:error, e} ->
          Logger.error("Failed to register all commands: #{e}")
      end
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

  # defp queue_command(name, module) do
  #   # scope = 1_159_695_388_865_998_848
  #   # scope = :global

  #   scope = Application.get_env(:octocon, :nostrum_scope)

  #   Logger.info("Running with channel scope: #{inspect(scope)}")

  #   case Nosedrum.Storage.Dispatcher.queue_command(name, module, scope) do
  #     {:ok, _} -> Logger.info("Registered '#{name}' command")
  #     {:error, e} -> Logger.error("Failed to register '#{name}' command: #{e}")
  #   end

  #   Process.sleep(3000)
  # end
end
