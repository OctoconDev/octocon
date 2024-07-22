defmodule OctoconDiscord.LastMessageManager do
  @moduledoc false

  @cache OctoconDiscord.Cache.LastMessage

  def get(discord_id) do
    Cachex.get!(@cache, to_string(discord_id))
  end

  def clear(discord_id) do
    Cachex.del!(@cache, to_string(discord_id))
  end

  def update(discord_id, data) do
    Cachex.put!(@cache, to_string(discord_id), data)
  end
end
