defmodule Octocon.ChannelBlacklists do
  @moduledoc """
  The ChannelBlacklists context.
  """

  import Ecto.Query, warn: false
  alias Octocon.Repo

  alias Octocon.ChannelBlacklists.ChannelBlacklist

  @doc """
  Returns a list of **all** channel blacklist entries. This is a dangerous, long-running operation and should be used
  with caution.
  """
  def list_channel_blacklists do
    Repo.all(ChannelBlacklist)
  end

  @doc false
  def list_channel_blacklists_bare do
    Repo.all(from(c in ChannelBlacklist, select: c.channel_id))
  end

  @doc """
  Gets all channel blacklists for a given guild.
  """
  def list_channel_blacklists_by_guild(guild_id) do
    Repo.all(from(c in ChannelBlacklist, where: c.guild_id == ^to_string(guild_id)))
  end

  @doc """
  Given a Discord channel ID, returns whether or not that channel is blacklisted.

  This should always be backed by a `OctoconDiscord.ChannelBlacklistManager` cache, as it is a latency-sensitive operation.
  """
  def is_channel_blacklisted?(channel_id) do
    Repo.exists?(from(c in ChannelBlacklist, where: c.channel_id == ^to_string(channel_id)))
  end

  @doc """
  Creates a channel blacklist given the desired `attrs`.
  """
  def create_channel_blacklist(attrs \\ %{}) do
    %ChannelBlacklist{}
    |> change_channel_blacklist(attrs)
    |> Repo.insert()
  end

  @doc """
  Deletes a channel blacklist given the entire struct.
  """
  def delete_channel_blacklist(%ChannelBlacklist{} = channel_blacklist) do
    Repo.delete(channel_blacklist)
  rescue
    _ in Ecto.StaleEntryError -> :ok
  end

  @doc """
  Builds a changeset based on the given `Octocon.ChannelBlacklists.ChannelBlacklist` struct and `attrs` to change.
  """
  def change_channel_blacklist(%ChannelBlacklist{} = channel_blacklist, attrs \\ %{}) do
    ChannelBlacklist.changeset(channel_blacklist, attrs)
  end
end
