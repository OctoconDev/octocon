defmodule Octocon.Journals.AlterJournalEntry do
  @moduledoc """
  An alter-specific journal entry. It consists of:

  - A user ID (7-character alphanumeric lowercase string)
  - An alter ID (integer up to 32,767)
  - A title (up to 100 characters)
  - Content (up to 20,000 characters)
  - A color (optional, a hexadecimal color code, e.g. `#ff0000`)
  """

  use Ecto.Schema
  import Ecto.Changeset

  @primary_key false

  schema "alter_journals" do
    field :id, Ecto.UUID, primary_key: true
    field :user_id, :string
    field :alter_id, :integer

    field :title, :string
    field :content, :string
    field :color, :string
    field :pinned, :boolean, default: false
    field :locked, :boolean, default: false

    belongs_to :user, Octocon.Accounts.User,
      foreign_key: :user_id,
      define_field: false

    belongs_to :alter, Octocon.Alters.Alter,
      foreign_key: :alter_id,
      define_field: false

    timestamps()
  end

  @doc """
  Builds a changeset based on the given `Octocon.Journals.AlterJournalEntry` struct and `attrs` to change.
  """
  def changeset(alter_journal_entry, attrs) do
    alter_journal_entry
    |> cast(attrs, [:title, :content, :color, :pinned, :locked])
    |> validate_length(:title, max: 100)
    |> validate_length(:content, max: 20_000)
    |> validate_format(:color, ~r/^#[0-9a-fA-F]{6}$/)
    |> validate_required([:title])
  end
end
