defmodule Octocon.ChannelBlacklists.ChannelBlacklist do
  @moduledoc """
  A channel blacklist entry. This is used to prevent the Octocon bot from proxying messages
  in a given channel.

  Storing the guild ID here is not strictly necessary, but is useful for database indexing by guild.
  """
  use Ecto.Schema
  import Ecto.Changeset

  @primary_key false

  schema "channel_blacklists" do
    field :guild_id, :string
    field :channel_id, :string, primary_key: true

    timestamps()
  end

  @doc """
  Builds a changeset based on the given `Octocon.ChannelBlacklists.ChannelBlacklist` struct and `attrs` to change.
  """
  def changeset(channel_blacklist, attrs) do
    channel_blacklist
    |> cast(attrs, [:guild_id, :channel_id])
    |> validate_required([:guild_id, :channel_id])
    |> validate_format(:guild_id, ~r/^\d{17,22}$/)
    |> validate_format(:channel_id, ~r/^\d{17,22}$/)
  end
end
