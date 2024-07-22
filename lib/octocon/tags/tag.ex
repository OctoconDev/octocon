defmodule Octocon.Tags.Tag do
  @moduledoc """
  A tag (folder) for categorizing alters. Each alter can be associated with multiple tags. It consists of:

  - A user ID (7-character alphanumeric lowercase string)
  - A tag ID (UUID)
  - A name (up to 100 characters)
  - A color (a hexadecimal color code, e.g. `#ff0000`)
  - A security level (enum, one of `:public`, `:friends_only`, `:trusted_only`, or `:private`)
  - A list of alter IDs that are associated with this tag (virtual, populated by the data layer)
  """
  use Ecto.Schema
  import Ecto.Changeset

  @primary_key false

  schema "tags" do
    field :id, Ecto.UUID, primary_key: true
    field :user_id, :string

    field :name, :string
    field :description, :string
    field :color, :string

    field :security_level, Ecto.Enum,
      values: [public: 0, friends_only: 1, trusted_only: 2, private: 3],
      default: :private

    field :parent_tag_id, Ecto.UUID

    field :alters, {:array, :integer}, virtual: true

    belongs_to :user, Octocon.Accounts.User,
      foreign_key: :user_id,
      define_field: false

    belongs_to :parent_tag, __MODULE__,
      foreign_key: :parent_tag_id,
      define_field: false

    timestamps()
  end

  @doc """
  Builds a changeset based on the given `Octocon.Tags.Tag` struct and `attrs` to change.
  """
  def changeset(tag, attrs) do
    tag
    |> cast(attrs, [:name, :color, :description, :security_level, :parent_tag_id])
    |> validate_length(:name, max: 100)
    |> validate_length(:description, max: 1000)
    |> validate_format(:color, ~r/^#[0-9a-fA-F]{6}$/)
    |> validate_required([:name])
  end
end
