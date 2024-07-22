defmodule Octocon.ServerSettings.ServerSettingsEntry do
  @moduledoc """
  A server settings entry for a given Discord guild. It consists of:

  - A Discord guild ID (a 17-22 character numeric string)
  - A data field (an embedded schema containing server settings)

  This data currently includes:
  - A log channel ID (a 17-22 character numeric string)
  - A flag to force system tags on all proxied messages
  """
  use Ecto.Schema
  import Ecto.Changeset

  @primary_key false

  schema "server_settings" do
    field :guild_id, :string, primary_key: true

    embeds_one :data, ServerSettingsData, primary_key: false, on_replace: :delete do
      field :log_channel, :string
      field :force_system_tags, :boolean, default: false

      field :proxy_disabled_users, {:array, :string}, default: []
    end

    timestamps()
  end

  @doc """
  Builds a changeset based on the given `Octocon.ServerSettings.ServerSettingsEntry` struct and `attrs` to change.
  """
  def changeset(%__MODULE__{} = server_settings_entry, attrs \\ %{}) do
    server_settings_entry
    |> cast(attrs, [:guild_id])
    |> cast_embed(:data, required: true, with: &data_changeset/2)
    |> unique_constraint([:guild_id], name: "server_settings_pkey")
    |> validate_required([:guild_id])
  end

  @doc """
  Builds a changeset based on the given `Octocon.ServerSettings.ServerSettingsData` struct and `attrs` to change.
  """
  def data_changeset(data, attrs \\ %{}) do
    data
    |> cast(attrs, [:log_channel, :force_system_tags, :proxy_disabled_users])
    |> validate_required([:force_system_tags])
  end
end
