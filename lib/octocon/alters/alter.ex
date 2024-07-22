defmodule Octocon.Alters.Alter do
  @moduledoc """
  An alter represents a single alter within a system (user). It consists of:

  - An ID (integer up to 32,767)
  - An associated user ID (7-character alphanumeric lowercase string)
  - A name (optional, up to 80 characters)
  - Pronouns (optional, up to 50 characters)
  - A description (optional, up to 3,000 characters)
  - A security level (enum, one of `:public`, `:friends_only`, `:trusted_only`, or `:private`)
  - An alias (optional, up to 80 characters; used for Discord)
  - An avatar URL (optional)
  - Extra images (optional, up to 3 URLs, currently unused)
  - A color (optional, a hex color code, e.g. `#ff0000`)
  - Discord proxies (optional, an array of strings in the format `prefixtextsuffix`)
  - Custom fields (optional, a map of `Octocon.Accounts.Field` IDs to values)
  """

  use Ecto.Schema
  import Ecto.Changeset

  @primary_key false

  @dropped_fields [
    :__meta__,
    :fronts,
    :user,
    :global_journals,
    :tags
  ]

  schema "alters" do
    field :id, :id, primary_key: true
    field :user_id, :string, primary_key: true

    field :name, :string
    field :pronouns, :string
    field :description, :string

    field :alias, :string

    field :security_level, Ecto.Enum,
      values: [public: 0, friends_only: 1, trusted_only: 2, private: 3],
      default: :private

    field :avatar_url, :string
    field :extra_images, {:array, :string}, default: []
    field :color, :string

    # Discord
    field :discord_proxies, {:array, :string}, default: []
    field :proxy_name, :string

    belongs_to :user, Octocon.Accounts.User,
      foreign_key: :user_id,
      define_field: false

    embeds_many :fields, Octocon.Alters.Field
    has_many :fronts, Octocon.Fronts.Front, foreign_key: :alter_id, references: :id

    many_to_many :global_journals, Octocon.Journals.GlobalJournalEntry,
      join_through: Octocon.Journals.GlobalJournalAlters,
      join_keys: [alter_id: :id, global_journal_id: :id]

    many_to_many :tags, Octocon.Tags.Tag,
      join_through: Octocon.Tags.AlterTag,
      join_keys: [alter_id: :id, tag_id: :id]

    timestamps()
  end

  defp global_validations(changeset) do
    changeset
    |> validate_format(:user_id, ~r/^[a-z]{7}$/)
    |> validate_format(:color, ~r/^#[0-9a-fA-F]{6}$/)
    |> validate_required(:id)
    |> validate_required(:user_id)
    |> validate_length(:name, max: 80)
    |> validate_length(:proxy_name, max: 80)
    |> validate_length(:pronouns, max: 50)
    |> validate_length(:description, max: 3000)
    |> validate_length(:extra_images, max: 3)
    # |> validate_length(:alias, max: 80)
    |> validate_format(:alias, ~r/^(?![\s\d])[^\n]{1,80}$/)
    |> foreign_key_constraint(:user_id)

    # |> unique_constraint([:user_id, :alias], name: :alters_user_id_alias_index)

    # |> unique_constraint([:user_id, :id])
  end

  @doc """
  Returns the list of fields that are not to be included during serialization (mostly Ecto metadata).
  """
  def dropped_fields, do: @dropped_fields

  @doc """
  Builds a changeset based on the given `Octocon.Alters.Alter` struct and `attrs` to change.
  """
  def changeset(alter, attrs) do
    alter
    |> cast(attrs, [
      :id,
      :user_id,
      :name,
      :pronouns,
      :description,
      :security_level,
      :alias,
      :avatar_url,
      :extra_images,
      :color,
      :discord_proxies,
      :proxy_name
    ])
    |> cast_embed(:fields)
    |> global_validations()
  end
end
