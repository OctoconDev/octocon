defmodule Octocon.ProxyCache.ProxyCacheItem do
  @moduledoc """
  A global cache item for a user's proxy data. This is used for providing a single source of truth
  for a user's proxy data across all nodes in the Octocon cluster, since a user can be on multiple
  shards, which can be on different nodes. It consists of:

  - A user ID (7-character alphanumeric lowercase string)
  - The cached data (an Erlang term in the form of raw binary data to be deserialized through :erlang.binary_to_term/1)
  """
  use Ecto.Schema
  import Ecto.Changeset

  @primary_key false

  schema "proxy_cache_items" do
    field :discord_id, :string, primary_key: true
    field :data, :binary
  end

  @doc false
  def changeset(pc_item, attrs) do
    pc_item
    |> cast(attrs, [:discord_id, :data])
    |> validate_required([:discord_id, :data])
  end
end
