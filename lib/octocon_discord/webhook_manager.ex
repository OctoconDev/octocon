defmodule OctoconDiscord.WebhookManager do
  @moduledoc false
  alias Nostrum.Api

  @proxy_name "Octocon Proxy"
  @timeout :timer.seconds(5)

  def get_webhook(channel_id) do
    task = Task.async(fn -> do_fetch(channel_id) end)

    Task.await(task, @timeout)
    # case Cachex.fetch!(OctoconDiscord.Cache.Webhooks, channel_id) do
    #   %{id: _, token: _} = result -> result
    #   _ -> nil
    # end
  end

  defp do_fetch(channel_id) do
    case Cachex.fetch!(OctoconDiscord.Cache.Webhooks, channel_id) do
      %{id: _, token: _} = result -> result
      _ -> nil
    end
  end

  def cache_function(channel_id) do
    case Api.get_channel_webhooks(channel_id) do
      {:ok, webhooks} ->
        case Enum.find(webhooks, fn webhook -> webhook.name == @proxy_name end) do
          webhook when is_map(webhook) ->
            {:commit, %{id: webhook.id, token: webhook.token}}

          nil ->
            {:ok, webhook} = Api.create_webhook(channel_id, %{name: @proxy_name})
            {:commit, %{id: webhook.id, token: webhook.token}}
        end

      _ ->
        {:ignore, nil}
    end
  end
end
