defmodule Octocon.ProxyCache do
  @moduledoc """
  The ProxyCache context.
  """

  import Ecto.Query, warn: false

  alias Octocon.ProxyCache.ProxyCacheItem
  alias Octocon.Repo

  def get_proxy_cache_item(discord_id) do
    query =
      ProxyCacheItem
      |> where([p], p.discord_id == ^discord_id)
      |> select([p], p.data)

    Repo.one(query)
  end

  def get_proxy_cache_item_struct(discord_id) do
    query =
      ProxyCacheItem
      |> where([p], p.discord_id == ^discord_id)

    Repo.one(query)
  end

  def upsert_proxy_cache_item(discord_id, data) when is_binary(discord_id) do
    encoded_data = :erlang.term_to_binary(data)

    case get_proxy_cache_item_struct(discord_id) do
      nil ->
        Repo.insert!(%ProxyCacheItem{discord_id: discord_id, data: encoded_data})

      item ->
        item
        |> change_proxy_cache_item(%{data: encoded_data})
        |> Repo.update()
    end
  end

  def delete_proxy_cache_item(nil), do: :ok

  def delete_proxy_cache_item(discord_id) when is_binary(discord_id) do
    query =
      ProxyCacheItem
      |> where([p], p.discord_id == ^discord_id)

    Repo.delete_all(query)
  end

  def wipe_proxy_cache do
    Repo.delete_all(ProxyCacheItem)
  end

  def change_proxy_cache_item(%ProxyCacheItem{} = proxy_cache_item, attrs \\ %{}) do
    ProxyCacheItem.changeset(proxy_cache_item, attrs)
  end
end
