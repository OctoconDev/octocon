defmodule OctoconDiscord.Components.ReproxyHandler do
  @moduledoc false
  use GenServer

  import OctoconDiscord.Proxy

  alias Nostrum.Api

  alias Octocon.Alters

  alias OctoconDiscord.Utils
  alias OctoconDiscord.Commands.Messages.Reproxy.NostrumShim

  @table :reproxy_handler

  def start_link([]) do
    GenServer.start_link(__MODULE__, [], name: __MODULE__)
  end

  def handle_init(user_id, raw_message, db_message) do
    uid = :erlang.unique_integer([:positive])

    data = %{
      user_id: user_id,
      raw_message: raw_message,
      db_message: db_message
    }

    :ets.insert(@table, {uid, data})

    Process.send_after(__MODULE__, {:drop, uid}, :timer.minutes(10))

    [
      type: :modal,
      title: "Reproxy message",
      custom_id: "reproxy|confirm|#{uid}",
      components: [
        text_input(
          id: "text",
          label: "New text",
          min_length: 1,
          max_length: 2000,
          placeholder: "Leave blank to reproxy with the same text."
        ),
        text_input(
          id: "alter",
          label: "Alter ID/alias",
          min_length: 1,
          max_length: 80,
          placeholder: "Leave blank to reproxy as the same alter."
        )
      ]
    ]
  end

  def handle_interaction("confirm", uid, interaction) do
    data =
      :ets.lookup(@table, uid)
      |> hd()
      |> elem(1)

    responses = parse_responses(interaction)

    result =
      try_reproxy_message(data, responses)
      |> Enum.into(%{})
      |> Map.drop([:ephemeral?])
      |> Map.put(:flags, 64)

    :ets.delete(@table, uid)

    a = %{
      type: 4,
      data: result
    }

    Api.create_interaction_response(interaction, a)
  end

  defp try_reproxy_message(%{
    raw_message: raw_message,
    db_message: db_message
  } = data, %{text: text, alter: idalias}) do
    system_id = db_message.system_id

    new_content = cond do
      String.trim(text) == "" -> raw_message.content
      true -> text
    end

    Utils.with_id_or_alias(idalias, fn
      nil ->
        alter_id = db_message.alter_id

        edit_message(data, %{
          system_id: system_id,
          alter_id: alter_id,
          text: new_content
        })
      alter_identity ->
        alter_id = Alters.resolve_alter({:system, system_id}, alter_identity)
        if alter_id == false do
          Utils.error_embed("You don't have an alter with ID or alias `#{alter_identity}`.")
        else
          reproxy_message(data, %{
            system_id: system_id,
            alter_id: alter_id,
            text: new_content
          })
        end
    end, true)
  end
  
  defp edit_message(%{
    user_id: user_id,
    raw_message: raw_message
  }, %{
    system_id: system_id,
    alter_id: alter_id,
    text: text
  }) do
    case with_proxy_prerequisites(user_id, raw_message, fn %{
      message: message,
      webhook: webhook,
      proxy_data: proxy_data,
      thread_id: thread_id,
      server_settings: server_settings
    } ->
      send_proxy_message(%{
        webhook: webhook,
        message: %{message | content: text, author: %{message.author | id: user_id}},
        alter: {system_id, alter_id},
        proxy_data: proxy_data,
        thread_id: thread_id,
        server_settings: server_settings
      }, true, fn id, token, data ->
        NostrumShim.edit_webhook_message(id, token, message.id, Map.put(data, :embeds, raw_message.embeds))
      end)
    end) do
      :no_proxy -> Utils.error_embed("Failed to reproxy the message.")
      :ok -> Utils.success_embed("Message reproxied!")
    end
  end

  defp reproxy_message(%{
    user_id: user_id,
    raw_message: raw_message
  }, %{
    system_id: system_id,
    alter_id: alter_id,
    text: text
  }) do
    case with_proxy_prerequisites(user_id, raw_message, fn %{
      message: message,
      webhook: webhook,
      proxy_data: proxy_data,
      thread_id: thread_id,
      server_settings: server_settings
    } ->
      send_proxy_message(%{
        webhook: webhook,
        message: %{message | content: text, author: %{message.author | id: user_id}},
        alter: {system_id, alter_id},
        proxy_data: proxy_data,
        thread_id: thread_id,
        server_settings: server_settings
      }, false)
    end) do
      :no_proxy -> Utils.error_embed("Failed to reproxy the message.")
      :ok -> Utils.success_embed("Message reproxied!")
    end
  end

  def drop(uid) do
    :ets.delete(@table, uid)
  end

  def init([]) do
    :ets.new(@table, [
      :set,
      :named_table,
      :public,
      read_concurrency: true,
      write_concurrency: :auto,
      decentralized_counters: true
    ])

    {:ok, %{}}
  end

  def handle_info({:drop, uid}, state) do
    :ets.delete(@table, uid)
    {:noreply, state}
  end

  defp parse_responses(%{data: %{components: components}}) do
    components
    |> Stream.map(&List.first(&1.components))
    |> Stream.map(fn %{custom_id: id, value: value} ->
      {String.to_atom(id), value}
    end)
    |> Enum.into(%{})
  end

  defp text_input(opts) do
    type =
      case Keyword.get(opts, :type, :short) do
        :short -> 1
        :long -> 2
      end

    %{
      type: 1,
      components: [
        %{
          type: 4,
          custom_id: Keyword.get(opts, :id),
          style: type,
          label: Keyword.get(opts, :label),
          min_length: Keyword.get(opts, :min_length),
          max_length: Keyword.get(opts, :max_length),
          placeholder: Keyword.get(opts, :placeholder),
          required: false
        }
      ]
    }
  end
end
