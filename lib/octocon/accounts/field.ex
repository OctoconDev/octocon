defmodule Octocon.Accounts.Field do
  @moduledoc """
  A field represents a "custom field" in a system. It consists of:

  - A name (string, up to 100 characters)
  - A type (enum, one of `:text`, `:number`, or `:boolean`)
  - A security level (enum, one of `:public`, `:friends_only`, `:trusted_only`, or `:private`)

  Alters loosely reference fields by their ID in their own `fields` map, which
  is to be cross-referenced with the account fields by a frontend client.
  """
  use Ecto.Schema
  import Ecto.Changeset

  @primary_key {:id, Ecto.UUID, autogenerate: true}

  embedded_schema do
    field :name, :string
    field :type, Ecto.Enum, values: [text: 0, number: 1, boolean: 2], default: :text
    field :locked, :boolean, default: false

    field :security_level, Ecto.Enum,
      values: [public: 0, friends_only: 1, trusted_only: 2, private: 3],
      default: :private
  end

  @doc """
  Builds a changeset based on the given `Octocon.Accounts.Field` struct and `attrs` to change.
  """
  def changeset(struct, attrs \\ %{})

  def changeset(nil, attrs), do: changeset(%__MODULE__{}, attrs)

  def changeset(%__MODULE__{} = field, attrs) do
    field
    |> cast(attrs, [:name, :type, :security_level, :locked])
    |> validate_required([:name, :type, :security_level])
    |> validate_length(:name, min: 1, max: 100)
    |> validate_inclusion(:type, [:text, :number, :boolean])
    |> validate_inclusion(:security_level, [:public, :friends_only, :trusted_only, :private])
  end
end
