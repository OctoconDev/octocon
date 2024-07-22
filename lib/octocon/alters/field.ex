defmodule Octocon.Alters.Field do
  @moduledoc """
  A field value associating an `Octocon.Accounts.Field` with a value for the given alter.
  """
  use Ecto.Schema
  import Ecto.Changeset

  @primary_key {:id, Ecto.UUID, autogenerate: false}

  embedded_schema do
    field :value, :string
  end

  @doc """
  Builds a changeset based on the given `Octocon.Alters.Field` struct and `attrs` to change.
  """
  def changeset(%__MODULE__{} = field, attrs \\ %{}) do
    field
    |> cast(attrs, [:value])
    |> validate_required([:value])
  end
end
