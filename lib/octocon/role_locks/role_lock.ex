defmodule Octocon.RoleLocks.RoleLock do
  @moduledoc """
  A role lock entry. This is used to lock the Octocon bot from proxying members without a given role
  """
  use Ecto.Schema
  import Ecto.Changeset

  @primary_key {:guild_id, :string, autogenerate: false}

  schema "role_locks" do
    field :role_id, :string

    timestamps()
  end

  @doc """
  Builds a changeset based on the given `Octocon.RoleLocks.RoleLock` struct and `attrs` to change.
  """
  def changeset(role_lock, attrs) do
    role_lock
    |> cast(attrs, [:guild_id, :role_id])
    |> validate_required([:guild_id, :role_id])
    |> validate_format(:guild_id, ~r/^\d{17,22}$/)
    |> validate_format(:role_id, ~r/^\d{17,22}$/)
    |> unique_constraint(:guild_id)
  end
end
