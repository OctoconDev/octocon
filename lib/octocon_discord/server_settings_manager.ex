defmodule OctoconDiscord.ServerSettingsManager do
  alias Octocon.ServerSettings, as: Persistence
  @cache OctoconDiscord.Cache.ServerSettings

  def get_settings(guild_id) do
    guild_id = to_string(guild_id)
    Cachex.fetch!(@cache, guild_id)
  end

  def create_settings(guild_id) do
    guild_id = to_string(guild_id)

    case Persistence.create_server_settings(guild_id) do
      {:ok, _} ->
        Cachex.update(@cache, guild_id, %{})
        :ok

      error ->
        error
    end
  end

  def edit_settings(guild_id, data) do
    guild_id = to_string(guild_id)

    case Persistence.edit_server_settings(guild_id, data) do
      {:ok, struct} ->
        settings = Map.from_struct(struct.data)
        Cachex.update(@cache, guild_id, settings)

        :ok

      error ->
        error
    end
  end

  def delete_settings(guild_id) do
    guild_id = to_string(guild_id)

    case Persistence.delete_server_settings(guild_id) do
      {:ok, _} ->
        invalidate_cache(guild_id)
        :ok

      error ->
        error
    end
  end

  def invalidate_cache(guild_id) do
    guild_id = to_string(guild_id)
    Cachex.del!(@cache, guild_id)
  end

  def cache_function(guild_id) do
    guild_id = to_string(guild_id)

    case Persistence.get_server_settings_data(guild_id) do
      nil ->
        {:ignore, nil}

      settings ->
        Cachex.update(@cache, guild_id, settings)
        {:commit, Map.from_struct(settings)}
    end

    # {:ok, webhooks} = Api.get_channel_webhooks(channel_id)

    # case Enum.find(webhooks, fn webhook -> webhook.name == @proxy_name end) do
    #   webhook when is_map(webhook) ->
    #     {:commit, %{id: webhook.id, token: webhook.token}}

    #   nil ->
    #     {:ok, webhook} = Api.create_webhook(channel_id, %{name: @proxy_name})
    #     {:commit, %{id: webhook.id, token: webhook.token}}
    # end
  end
end
