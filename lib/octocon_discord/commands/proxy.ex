defmodule OctoconDiscord.Proxy do
  @moduledoc false

  require Logger

  alias Octocon.Alters

  alias Octocon.{
    Alters,
    Messages
  }

  alias OctoconDiscord.{
    ChannelBlacklistManager,
    ProxyCache,
    ServerSettingsManager,
    Utils
  }

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

  def with_proxy_prerequisites(user_id, message, fun) do
    # ProxyCache is also responsible for keeping track of users who don't have an account
    case ProxyCache.get(to_string(user_id)) do
      {:error, :no_user} ->
        # User doesn't have an Octocon account, ignore
        :no_proxy

      {:ok, proxy_data} ->
        {channel_id, parent_id, thread_id} = check_thread(message)

        # Fast path: if the channel is blacklisted, don't bother checking anything else
        unless ChannelBlacklistManager.is_blacklisted?(
                 to_string(channel_id),
                 to_string(parent_id)
               ) do
          webhook = OctoconDiscord.WebhookManager.get_webhook(channel_id)

          server_settings = ServerSettingsManager.get_settings(message.guild_id)

          if webhook == nil do
            :no_proxy
          else
            case server_settings do
              nil ->
                fun.(%{
                  message: message,
                  webhook: webhook,
                  proxy_data: proxy_data,
                  thread_id: thread_id,
                  server_settings: server_settings
                })

                :ok

              # Check whether this user has proxying disabled in this guild (this is handled by ServerSettingsManager)
              %{proxy_disabled_users: disabled_users} ->
                if Enum.member?(disabled_users, to_string(user_id)) do
                  :no_proxy
                else
                  fun.(%{
                    message: message,
                    webhook: webhook,
                    proxy_data: proxy_data,
                    thread_id: thread_id,
                    server_settings: server_settings
                  })

                  :ok
                end
            end
          end
        end
    end
  end

  # Sends a proxied message to Discord
  def send_proxy_message(
        %{
          webhook: webhook,
          message: message,
          alter: {system_id, alter_id},
          proxy_data: %{
            system_tag: system_tag,
            show_system_tag: show_system_tag,
            show_proxy_pronouns: show_proxy_pronouns
          },
          thread_id: thread_id,
          server_settings: server_settings
        },
        is_reproxy,
        proxy_fun \\ fn id, token, data -> Nostrum.Api.execute_webhook(id, token, data, true) end
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
        send_proxy_message_raw(
          %{
            webhook: webhook,
            message: message,
            webhook_data: webhook_data,
            server_settings: server_settings,
            context: context
          },
          is_reproxy,
          proxy_fun
        )

      files ->
        # Otherwise, we need to download the files and send them along with the message
        send_proxy_message_with_files(
          %{
            webhook: webhook,
            message: message,
            webhook_data: webhook_data,
            files: files,
            server_settings: server_settings,
            context: context
          },
          is_reproxy,
          proxy_fun
        )
    end
  end

  # Sends a proxied message to Discord with files
  defp send_proxy_message_with_files(
         %{
           webhook: webhook,
           message: message,
           webhook_data: webhook_data,
           files: files,
           server_settings: server_settings,
           context: context
         },
         is_reproxy,
         proxy_fun
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
    send_proxy_message_raw(
      %{
        webhook: webhook,
        message: message,
        webhook_data: webhook_data,
        server_settings: server_settings,
        context: context
      },
      is_reproxy,
      proxy_fun
    )
  end

  # Sends a proxied message to Discord with the given webhook data
  defp send_proxy_message_raw(
         %{
           webhook: webhook,
           message: message,
           webhook_data: webhook_data,
           server_settings: server_settings,
           context: context
         },
         is_reproxy,
         proxy_fun
       ) do
    webhook_task =
      Task.async(fn ->
        result_message = proxy_fun.(webhook.id, webhook.token, webhook_data)

        case result_message do
          {:ok, %{id: message_id}} ->
            unless is_reproxy do
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
            end

          {:error, error} ->
            Logger.error("Failed to send proxy message: #{inspect(error)}")
        end
      end)

    delete_task =
      Task.async(fn ->
        unless(is_reproxy) do
          Api.delete_message!(message.channel_id, message.id)
        end
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
