defmodule OctoconDiscord.Events.MessageCreate do
  @moduledoc """
  Handles MESSAGE_CREATE events from Discord, mostly for proxying messages.

  TODO: Refactor this module to be more readable and maintainable; feature creep bloated this to all hell.
  """

  require Logger

  alias Octocon.{
    Alters,
    Fronts
  }

  alias OctoconDiscord.{
    ProxyCache,
    Utils
  }

  import OctoconDiscord.Proxy

  @accepted_message_types [
    # DEFAULT
    0,
    # REPLY
    19
  ]

  def handle(%{content: "octo:meow", channel_id: channel_id}) do
    Nostrum.Api.create_message!(channel_id,
      content: "Nya~!"
    )
  end

  # Ignore PluralKit commands
  def handle(%{content: "pk;" <> _}) do
    :ok
  end

  def handle(%{content: "Pk;" <> _}) do
    :ok
  end

  # Ignore PluralKit commands (alternative prefix)
  def handle(%{content: "pk!" <> _}) do
    :ok
  end

  def handle(%{content: "Pk!" <> _}) do
    :ok
  end

  # Ignore Tupperbox commands
  def handle(%{content: "tul!" <> _}) do
    :ok
  end

  # Ignore messages that start with "\"
  def handle(%{content: "\\" <> _}) do
    :ok
  end

  def handle(message)
      when message.author.bot == nil and
             message.guild_id != nil and
             message.type in @accepted_message_types do
    with_proxy_prerequisites(message.author.id, message, fn context ->
      proxy_message(context)
    end)
  end

  # Ignore other message types silently (pins, bot messages, etc.)
  def handle(_message), do: :ok

  defp proxy_message(%{
         message: message,
         webhook: webhook,
         proxy_data: proxy_data,
         thread_id: thread_id,
         server_settings: server_settings
       }) do
    # Check if the message matches a manual proxy first
    matched_proxy =
      get_proxy(%{
        proxy_list: proxy_data.proxies,
        message: message.content,
        proxy_data: proxy_data
      })

    case matched_proxy do
      nil ->
        # ...if not, check autoproxy
        case proxy_data.mode do
          :none ->
            # Autoproxy disabled, ignore
            :ok

          :front ->
            system_id = proxy_data.system_id

            case proxy_data.primary_front do
              nil ->
                # If there's no primary fronter, find the longest current fronter
                case Fronts.longest_current_fronter({:system, system_id}) do
                  nil ->
                    :ok

                  %{alter: %{id: alter_id}} ->
                    send_proxy_message(
                      %{
                        webhook: webhook,
                        message: message,
                        alter: {system_id, alter_id},
                        proxy_data:
                          Map.take(proxy_data, [
                            :system_tag,
                            :show_system_tag,
                            :show_proxy_pronouns
                          ]),
                        thread_id: thread_id,
                        server_settings: server_settings
                      },
                      false
                    )
                end

              alter_id ->
                send_proxy_message(
                  %{
                    webhook: webhook,
                    message: message,
                    alter: {system_id, alter_id},
                    proxy_data:
                      Map.take(proxy_data, [:system_tag, :show_system_tag, :show_proxy_pronouns]),
                    thread_id: thread_id,
                    server_settings: server_settings
                  },
                  false
                )
            end

          {:latch, :ready} ->
            # Latch mode, but no one has proxied yet
            :ok

          {:latch, alter_id} ->
            system_id = proxy_data.system_id

            send_proxy_message(
              %{
                webhook: webhook,
                message: message,
                alter: {system_id, alter_id},
                proxy_data:
                  Map.take(proxy_data, [:system_tag, :show_system_tag, :show_proxy_pronouns]),
                thread_id: thread_id,
                server_settings: server_settings
              },
              false
            )
        end

      {new_message, data} ->
        case proxy_data.mode do
          # Update latched alter if necessary
          {:latch, _} ->
            ProxyCache.update_mode(to_string(message.author.id), {:latch, elem(data, 1)})

          _ ->
            :ok
        end

        unless new_message == "" do
          send_proxy_message(
            %{
              webhook: webhook,
              message: %Nostrum.Struct.Message{message | content: new_message},
              alter: data,
              proxy_data:
                Map.take(proxy_data, [:system_tag, :show_system_tag, :show_proxy_pronouns]),
              thread_id: thread_id,
              server_settings: server_settings
            },
            false
          )
        end
    end
  end

  defp get_proxy(%{
         proxy_list: proxy_list,
         message: message,
         proxy_data: proxy_data
       }) do
    proxy =
      if proxy_data.case_insensitive_proxying do
        proxy_list
        |> Enum.find(fn {{prefix, suffix, _}, _} ->
          String.starts_with?(String.downcase(message), String.downcase(prefix)) &&
            String.ends_with?(String.downcase(message), String.downcase(suffix))
        end)
      else
        proxy_list
        |> Enum.find(fn {{prefix, suffix, _}, _} ->
          String.starts_with?(message, prefix) &&
            String.ends_with?(message, suffix)
        end)
      end

    case proxy do
      nil ->
        if proxy_data.ids_as_proxies do
          case String.split(message, "-", parts: 2) do
            [_] -> nil
            # If "IDs as proxies" mode is enabled, we'll try to get an ID/alias from the message
            [prefix, rest] -> parse_idalias(proxy_data.system_id, prefix, rest)
          end
        else
          nil
        end

      {{prefix, suffix, _}, data} ->
        # Matched a manual proxy!
        new_message =
          message
          |> String.slice(String.length(prefix)..-(String.length(suffix) + 1)//1)

        {new_message, data}
    end
  end

  # Parses an ID or alias from a message when "IDs as proxies" is enabled into an alter identity
  defp parse_idalias(system_id, prefix, message) do
    cond do
      Utils.alter_id_valid?(prefix) ->
        try_get_alter(system_id, {:id, Utils.parse_id!(prefix)}, message)

      Utils.alter_alias_valid?(prefix) ->
        try_get_alter(system_id, {:alias, prefix}, message)

      true ->
        nil
    end
  end

  # Tries to get an alter by ID or alias, returning nil if the alter doesn't exist
  defp try_get_alter(system_id, alter_identity, message) do
    case Alters.resolve_alter({:system, system_id}, alter_identity) do
      false -> nil
      alter_id -> {message, {system_id, alter_id}}
    end
  end
end
