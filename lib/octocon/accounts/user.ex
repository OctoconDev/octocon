defmodule Octocon.Accounts.User do
  @moduledoc """
  A user represents a single Octocon account. It consists of:

  - An ID (7-character alphanumeric lowercase string)
  - An email address (optional)
  - A Discord ID (optional)
  - A username (optional)
  - An avatar URL (optional)
  - A lifetime alter count (integer, used to generate new alter IDs without conflicts)
  - A primary front (integer ID of the primary fronting alter or nil)
  - An autoproxy mode (enum, one of `:off`, `:front`, or `:latch`)
  - A system tag (optional, a short string to identify the system, used on Discord)
  - A flag to show the system tag on Discord (boolean)
  - A flag to enable case-insensitive proxying (boolean)
  - A flag to show pronouns on proxied messages (boolean)

  A user MUST have either an email address, a Discord ID, or both, but not neither.
  """
  use Ecto.Schema
  import Ecto.Changeset

  @primary_key {:id, :string, autogenerate: false}

  defp generate_uuid do
    Nanoid.generate(7, "abcdefghijklmnopqrstuvwxyz")
  end

  schema "users" do
    field :avatar_url, :string
    field :discord_id, :string
    field :email, :string
    field :username, :string

    field :description, :string

    field :lifetime_alter_count, :integer, default: 0
    field :primary_front, :integer

    # Discord
    field :autoproxy_mode, Ecto.Enum, values: [off: 0, front: 1, latch: 2], default: :off
    field :system_tag, :string, default: nil
    field :show_system_tag, :boolean, default: false
    field :case_insensitive_proxying, :boolean, default: false
    field :show_proxy_pronouns, :boolean, default: true
    field :ids_as_proxies, :boolean, default: false

    has_many :alters, Octocon.Alters.Alter, foreign_key: :user_id, references: :id
    has_many :fronts, Octocon.Fronts.Front, foreign_key: :user_id, references: :id

    has_many :global_journals, Octocon.Journals.GlobalJournalEntry,
      foreign_key: :user_id,
      references: :id

    embeds_many :fields, Octocon.Accounts.Field, on_replace: :delete

    timestamps()
  end

  defp global_validations(changeset) do
    changeset
    |> validate_format(:id, ~r/^[a-z]{7}$/)
    |> validate_format(:email, ~r/@/)
    |> validate_format(:discord_id, ~r/^\d{17,22}$/)
    |> validate_length(:description, max: 3000)
    |> validate_length(:system_tag, max: 20)
    # Dear christ help me
    |> validate_format(:username, ~r/^[a-zA-Z0-9]([a-zA-Z0-9_\-.]{3,22})[a-zA-Z0-9]$/)
    # Make username not able to look like a system id (primary key)
    |> validate_format(:username, ~r/^(?:(?![a-z]{7}).*)$/)
    |> validate_inclusion(:primary_front, 1..32_767)
    |> unique_constraint(:email)
    |> unique_constraint(:discord_id)
    |> unique_constraint(:username)
  end

  @doc """
  Builds a changeset based on the given `Octocon.Accounts.User` struct and `attrs` to change.
  """
  def update_changeset(user, attrs \\ %{}) do
    user
    |> cast(attrs, [
      :id,
      :email,
      :discord_id,
      :avatar_url,
      :username,
      :lifetime_alter_count,
      :primary_front,
      :system_tag,
      :show_system_tag,
      :autoproxy_mode,
      :case_insensitive_proxying,
      :show_proxy_pronouns,
      :ids_as_proxies,
      :description
    ])
    |> cast_embed(:fields)
    |> global_validations()
  end

  @doc """
  Builds a changeset to create a new user with the given `discord_id` and extra `attrs`.
  """
  def create_from_discord_changeset(discord_id, attrs) do
    %__MODULE__{discord_id: to_string(discord_id), id: generate_uuid()}
    |> cast(attrs, [:id, :email, :discord_id, :username])
    |> validate_required([:id, :discord_id])
    |> global_validations()
  end

  @doc """
  Builds a changeset to create a new user with the given `email` and extra `attrs`.
  """
  def create_from_email_changeset(email, attrs) do
    %__MODULE__{email: email, id: generate_uuid()}
    |> cast(attrs, [:id, :email, :discord_id, :username])
    |> validate_required([:id, :email])
    |> global_validations()
  end
end
