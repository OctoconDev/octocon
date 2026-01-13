defmodule Octocon.RoleLocks do
  @moduledoc """
  The RoleLock context.
  """

  @region_specifier {:region, :nam}

  import Ecto.Query, warn: false
  alias Octocon.Repo
  alias Octocon.RoleLocks.RoleLock

  @doc """
  Returns a list of **all** role lock entries.
  Used by the RoleLockManager to hydrate the cache on startup.
  """
  def list_role_locks do
    Repo.all_regional(RoleLock, @region_specifier)
  end

  @doc """
  Gets the role lock for a specific guild.
  Returns `nil` if none exists.
  """
  def get_role_lock(guild_id) do
    Repo.one_regional(
      from(r in RoleLock, where: r.guild_id == ^to_string(guild_id)),
      @region_specifier
    )
  end

  @doc """
  Sets the role lock for a guild.

  Enforces that there is exactly one lock per guild by deleting any existing lock
  for this guild before inserting the new one.
  """
  def set_role_lock(attrs) do
    guild_id = attrs[:guild_id]
    delete_role_lock(guild_id)

    %RoleLock{}
    |> change_role_lock(attrs)
    |> Repo.insert_regional(@region_specifier)
  end

  @doc """
  Deletes the role lock for a specific guild ID.
  """
  def delete_role_lock(guild_id) when is_binary(guild_id) do
    Repo.delete_regional(%RoleLock{guild_id: guild_id}, @region_specifier)
    :ok
  rescue
    _ in Ecto.StaleEntryError -> :ok
  end

  @doc """
  Builds a changeset based on the given `RoleLock` struct and `attrs`.
  """
  def change_role_lock(%RoleLock{} = role_lock, attrs \\ %{}) do
    RoleLock.changeset(role_lock, attrs)
  end
end
