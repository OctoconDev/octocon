defmodule Octocon.Messages.Message do
  use Ecto.Schema
  import Ecto.Changeset

  @primary_key false

  schema "messages" do
    field :timestamp, :utc_datetime
    field :message_id, :string
    field :author_id, :string
    field :system_id, :string
    field :alter_id, :integer
  end

  def changeset(message, attrs) do
    message
    |> cast(attrs, [
      :timestamp,
      :message_id,
      :author_id,
      :system_id,
      :alter_id
    ])
    |> validate_required([
      :timestamp,
      :message_id,
      :author_id,
      :system_id,
      :alter_id
    ])
  end
end
