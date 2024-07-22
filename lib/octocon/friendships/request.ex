defmodule Octocon.Friendships.Request do
  @moduledoc """
  A friend request between two users. It consists of:

  - A sender (from) ID (7-character alphanumeric lowercase string)
  - A recipient (to) ID (7-character alphanumeric lowercase string)
  - A timestamp of when the request was sent
  """
  use Ecto.Schema
  import Ecto.Changeset

  @primary_key false

  schema "friend_requests" do
    field :from_id, :string, primary_key: true
    field :to_id, :string, primary_key: true

    field :date_sent, :utc_datetime

    belongs_to :from, Octocon.Accounts.User,
      foreign_key: :from_id,
      references: :id,
      define_field: false

    belongs_to :to, Octocon.Accounts.User,
      foreign_key: :to_id,
      references: :id,
      define_field: false

    timestamps()
  end

  @doc """
  Builds a changeset based on the given `Octocon.Friendships.Request` struct and `attrs` to change.
  """
  def changeset(request, attrs) do
    request
    |> cast(attrs, [:from_id, :to_id])
    |> validate_required([:from_id, :to_id])
    |> validate_format(:from_id, ~r/^[a-z]{7}$/)
    |> validate_format(:to_id, ~r/^[a-z]{7}$/)
    |> foreign_key_constraint(:from_id)
    |> foreign_key_constraint(:to_id)
  end
end
