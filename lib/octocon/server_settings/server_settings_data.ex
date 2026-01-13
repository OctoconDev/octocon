defmodule Octocon.ServerSettings.ServerSettingsData do
  @moduledoc """
  The data field of a server settings entry for a given Discord guild. It consists of:

  - A log channel ID (a 17-22 character numeric string)
  - A flag to force system tags on all proxied messages
  - A list of user IDs (17-22 character numeric strings) for whom proxying is disabled
  """
  use Ecto.Schema
  import Ecto.Changeset

  @primary_key false
  embedded_schema do
    field :log_channel, :string
    field :role_lock, :string
    field :force_system_tags, :boolean, default: false

    field :proxy_disabled_users, {:array, :string}, default: []
  end

  @doc """
  Builds a changeset based on the given `Octocon.ServerSettings.ServerSettingsData` struct and `attrs` to change.
  """
  def changeset(data, attrs \\ %{}) do
    data
    |> cast(attrs, [:log_channel, :role_lock, :force_system_tags, :proxy_disabled_users])
    |> validate_required([:force_system_tags])
  end
end
