defmodule OctoconDiscord.Events.MessageCreate do
  @moduledoc """
  Handles MESSAGE_CREATE events from Discord, mostly for proxying messages.

  TODO: Refactor this module to be more readable and maintainable; feature creep bloated this to all hell.
  """

  require Logger

  alias Octocon.{
    Alters,
    Fronts,
    Messages
  }

  alias OctoconDiscord.{
    ChannelBlacklistManager,
    LastMessageManager,
    ProxyCache,
    ServerSettingsManager,
    Utils
  }

  @accepted_message_types [
    # DEFAULT
    0,
    # REPLY
    19
  ]

  alias Nostrum.Api

  # Checks whether a message is in a thread or not
  # This uses the guild cache, but may send an API request if the guild isn't cached on this node for some reason
  defp check_thread(%Nostrum.Struct.Message{guild_id: guild_id, channel_id: channel_id}) do
    guild = Utils.get_cached_guild(guild_id)

    case Map.get(guild.threads, channel_id) do
      nil ->
        channel = Map.get(guild.channels, channel_id)
        {channel_id, channel.parent_id, nil}

      thread ->
        {thread.parent_id, nil, thread.id}
    end
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
    id = message.author.id

    # ProxyCache is also responsible for keeping track of users who don't have an account
    case ProxyCache.get(to_string(id)) do
      {:error, :no_user} ->
        # User doesn't have an Octocon account, ignore
        :ok

      {:ok, proxy_data} ->
        {channel_id, parent_id, thread_id} = check_thread(message)

        # Fast path: if the channel is blacklisted, don't bother checking anything else
        unless ChannelBlacklistManager.is_blacklisted?(
                 to_string(channel_id),
                 to_string(parent_id)
               ) do
          webhook = OctoconDiscord.WebhookManager.get_webhook(channel_id)

          server_settings = ServerSettingsManager.get_settings(message.guild_id)

          unless webhook == nil do
            case server_settings do
              nil ->
                proxy_message(message, webhook, proxy_data, thread_id, server_settings)

              # Check whether this user has proxying disabled in this guild (this is handled by ServerSettingsManager)
              %{proxy_disabled_users: disabled_users} ->
                if Enum.member?(disabled_users, to_string(id)) do
                  :ok
                else
                  proxy_message(message, webhook, proxy_data, thread_id, server_settings)
                end
            end
          end
        end
    end
  end

  # Ignore other message types silently (pins, bot messages, etc.)
  def handle(_message), do: :ok

  defp proxy_message(message, webhook, proxy_data, thread_id, server_settings) do
    # Check if the message matches a manual proxy first
    matched_proxy =
      get_proxy(proxy_data.proxies, message.content, proxy_data)

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
                      webhook,
                      message,
                      {system_id, alter_id},
                      Map.take(proxy_data, [:system_tag, :show_system_tag, :show_proxy_pronouns]),
                      thread_id,
                      server_settings
                    )
                end

              alter_id ->
                send_proxy_message(
                  webhook,
                  message,
                  {system_id, alter_id},
                  Map.take(proxy_data, [:system_tag, :show_system_tag, :show_proxy_pronouns]),
                  thread_id,
                  server_settings
                )
            end

          {:latch, :ready} ->
            # Latch mode, but no one has proxied yet
            :ok

          {:latch, alter_id} ->
            system_id = proxy_data.system_id

            send_proxy_message(
              webhook,
              message,
              {system_id, alter_id},
              Map.take(proxy_data, [:system_tag, :show_system_tag, :show_proxy_pronouns]),
              thread_id,
              server_settings
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
            webhook,
            %Nostrum.Struct.Message{message | content: new_message},
            data,
            Map.take(proxy_data, [:system_tag, :show_system_tag, :show_proxy_pronouns]),
            thread_id,
            server_settings
          )
        end
    end
  end

  defp get_proxy(proxy_list, message, proxy_data) do
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

  # Sends a proxied message to Discord
  # TODO: Make this independent of this module for e.g. reproxying
  defp send_proxy_message(
         webhook,
         message,
         {system_id, alter_id},
         %{
           system_tag: system_tag,
           show_system_tag: show_system_tag,
           show_proxy_pronouns: show_proxy_pronouns
         },
         thread_id,
         server_settings
       ) do
    {:ok, alter} =
      Alters.get_alter_by_id({:system, system_id}, {:id, alter_id}, [
        :name,
        :avatar_url,
        :pronouns,
        :color,
        :proxy_name
      ])

    final_tag =
      cond do
        system_tag == nil -> ""
        server_settings[:force_system_tags] == true or show_system_tag == true -> " #{system_tag}"
        true -> ""
      end

    parsed_pronouns =
      if show_proxy_pronouns == false do
        ""
      else
        case alter.pronouns do
          nil -> ""
          "" -> ""
          pronouns -> " (#{pronouns})"
        end
      end

    base_name = alter.proxy_name || alter.name

    name_length = String.length(base_name)
    tag_length = String.length(final_tag)

    truncated_pronouns =
      parsed_pronouns
      |> String.slice(0..(80 - tag_length - name_length - 1))

    final_username = "#{base_name}#{truncated_pronouns}#{final_tag}"

    webhook_data = %{
      content: message.content,
      username: final_username,
      avatar_url: alter.avatar_url,
      thread_id: thread_id,
      embeds:
        case message.message_reference do
          nil ->
            nil

          %{message_id: message_id} ->
            case Api.get_channel_message(message.channel_id, message_id) do
              {:ok, reply} ->
                [build_reply_embed(message, reply, alter.color)]

              _ ->
                nil
            end
        end
    }

    context = %{
      system_id: system_id,
      alter_id: alter_id,
      author_id: to_string(message.author.id)
    }

    case message.attachments do
      [] ->
        # If we have no attachments, we're done
        send_proxy_message_raw(webhook, message, webhook_data, server_settings, context)

      files ->
        # Otherwise, we need to download the files and send them along with the message
        send_proxy_message_with_files(
          webhook,
          message,
          webhook_data,
          files,
          server_settings,
          context
        )
    end
  end

  # Sends a proxied message to Discord with files
  defp send_proxy_message_with_files(
         webhook,
         message,
         webhook_data,
         files,
         server_settings,
         context
       ) do
    attachments =
      files
      |> Stream.filter(fn file -> file.size < 20_000_000 end)
      |> Stream.map(fn file -> Map.take(file, [:filename, :url]) end)
      |> Task.async_stream(
        fn %{filename: filename, url: url} ->
          req =
            Finch.build(:get, url)
            |> Finch.request(Octocon.Finch)

          case req do
            {:ok, %{body: body}} ->
              {:ok, %{name: filename, body: body}}

            {:error, error} ->
              {:error, error}
          end
        end,
        timeout: :timer.seconds(15),
        max_concurrency: 4
      )
      |> Stream.filter(fn
        {:ok, {:ok, _}} -> true
        _ -> false
      end)
      |> Stream.map(fn {:ok, {:ok, data}} -> data end)
      |> Enum.to_list()

    webhook_data =
      webhook_data
      |> Map.put(:files, attachments)
      |> Map.put(
        :flags,
        if attachments == [] do
          nil
        else
          case hd(attachments) do
            # This is a bit hacky, but it lets us tell Discord that we're sending a voice message
            %{name: "voice-message.ogg"} -> Bitwise.<<<(1, 13)
            _ -> nil
          end
        end
      )

    # Delegate to `send_proxy_message_raw` with the updated webhook data
    send_proxy_message_raw(webhook, message, webhook_data, server_settings, context)
  end

  # Sends a proxied message to Discord with the given webhook data
  defp send_proxy_message_raw(webhook, message, webhook_data, server_settings, context) do
    webhook_task =
      Task.async(fn ->
        result_message = Api.execute_webhook(webhook.id, webhook.token, webhook_data, true)

        case result_message do
          {:ok, %{id: message_id}} ->
            LastMessageManager.update(
              message.author.id,
              {message_id, message.channel_id, webhook_data.content, webhook_data.embeds}
            )

            spawn(fn ->
              attrs =
                context
                |> Map.put(:message_id, to_string(message_id))
                |> Map.put(:timestamp, Nostrum.Snowflake.creation_time(message_id))

              # Log the message in Timescale
              Messages.insert_message(attrs)
            end)

            spawn(fn ->
              log_proxy_message(
                message,
                message_id,
                server_settings
              )
            end)

          {:error, error} ->
            Logger.error("Failed to send proxy message: #{inspect(error)}")
        end
      end)

    delete_task =
      Task.async(fn ->
        Api.delete_message!(message.channel_id, message.id)
      end)

    # Bail if Discord rate-limits us or otherwise fails to send the message
    # This is especially useful to avoid holding attachments in RAM for too long
    Task.await_many([webhook_task, delete_task], :timer.seconds(10))
  end

  # Logs a proxied message to the log channel
  # If no log channel is set, or if the server doesn't have any settings in the database, ignore
  defp log_proxy_message(_, _, nil), do: :ok
  defp log_proxy_message(_, _, %{log_channel: nil}), do: :ok

  # Otherwise, proceed with logging
  defp log_proxy_message(
         %{
           guild_id: guild_id,
           content: content,
           channel_id: channel_id,
           author: %{id: author_id, avatar: avatar_hash}
         },
         message_id,
         %{log_channel: log_channel}
       ) do
    permalink = "https://discord.com/channels/#{guild_id}/#{channel_id}/#{message_id}"

    truncated_content =
      cond do
        content == nil or content == "" ->
          nil

        String.length(content) > 500 ->
          content
          |> String.slice(0..500)
          |> Kernel.<>("\n...")

        true ->
          content
      end

    result =
      Api.create_message(
        String.to_integer(log_channel),
        %{
          content: "",
          url: permalink,
          embeds: [
            %Nostrum.Struct.Embed{
              title: "Message proxied",
              timestamp: Nostrum.Snowflake.creation_time(message_id),
              fields: [
                %Nostrum.Struct.Embed.Field{
                  name: "Author",
                  value: "<@#{author_id}>",
                  inline: true
                },
                %Nostrum.Struct.Embed.Field{
                  name: "Permalink",
                  value: "[Jump to message](#{permalink})",
                  inline: true
                }
              ],
              thumbnail: %Nostrum.Struct.Embed.Thumbnail{
                url: Utils.get_avatar_url(author_id, avatar_hash)
              },
              color: Utils.hex_to_int("#0FBEAA"),
              description: truncated_content
            }
          ]
        }
      )

    case result do
      {:ok, _} ->
        :ok

      {:error, error} ->
        Logger.error("Failed to log proxied message: #{inspect(error)}")
    end
  end

  # Recreate replies as embeds
  defp build_reply_embed(message, reply, color) do
    %{
      author: reply_author,
      content: reply_content
    } = reply

    truncated_content =
      cond do
        reply_content == nil or reply_content == "" ->
          nil

        String.length(reply_content) > 75 ->
          trimmed =
            reply_content
            |> String.slice(0..75)

          spoiler_count =
            trimmed
            |> String.split("||")
            |> length()
            |> then(&Kernel.-(&1, 1))

          # If the spoiler count is odd, we need to add a spoiler tag to the end
          # This ensures spoilered content isn't accidentally revealed when truncating
          trimmed <> if rem(spoiler_count, 2) == 0, do: "...", else: "||..."

        true ->
          reply_content
      end

    %Nostrum.Struct.Embed{
      author: %Nostrum.Struct.Embed.Author{
        icon_url: Utils.get_avatar_url(reply_author.id, reply_author.avatar),
        name: "#{reply_author.username} ↩️"
      },
      description:
        "[Reply to:](https://discord.com/channels/#{message.guild_id}/#{reply.channel_id}/#{reply.id}) #{truncated_content}",
      color: Utils.hex_to_int(color)
    }
  end
end
