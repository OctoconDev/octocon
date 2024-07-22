defmodule Octocon.Friendships.Friendship do
  @moduledoc """
  A friendship between two users. This is used to track a relationship between users, and is always
  bidirectional (i.e. if a `Friendship` exists between user A and user B, there is also a `Friendship`
  between user B and user A). It consists of:

  - A user ID (7-character alphanumeric lowercase string)
  - A friend ID (7-character alphanumeric lowercase string)
  - A level (enum, one of `:friend` or `:trusted_friend`)
  - A timestamp of when the friendship was established
  """
  use Ecto.Schema
  import Ecto.Changeset

  @primary_key false

  schema "friendships" do
    field :user_id, :string, primary_key: true
    field :friend_id, :string, primary_key: true
    field :level, Ecto.Enum, values: [friend: 0, trusted_friend: 1], default: :friend

    field :since, :utc_datetime

    belongs_to :user, Octocon.Accounts.User,
      foreign_key: :user_id,
      references: :id,
      define_field: false

    belongs_to :friend, Octocon.Accounts.User,
      foreign_key: :friend_id,
      references: :id,
      define_field: false

    timestamps()
  end

  @doc """
  Builds a changeset based on the given `Octocon.Friendships.Friendship` struct and `attrs` to change.
  """
  def changeset(friendship, attrs) do
    friendship
    |> cast(attrs, [:user_id, :friend_id, :level])
    |> validate_required([:user_id, :friend_id, :level])
    |> validate_format(:user_id, ~r/^[a-z]{7}$/)
    |> validate_format(:friend_id, ~r/^[a-z]{7}$/)
  end
end
