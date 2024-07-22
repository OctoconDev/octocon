defmodule Octocon.ServerSettings do
  @moduledoc """
  The ServerSettings context.
  """

  import Ecto.Query, warn: false
  alias Octocon.Repo

  alias Octocon.ServerSettings.ServerSettingsEntry

  def get_server_settings(guild_id) do
    query = from(s in ServerSettingsEntry, where: s.guild_id == ^guild_id)

    Repo.one(query)
  end

  def get_server_settings_data(guild_id) do
    query = from(s in ServerSettingsEntry, where: s.guild_id == ^guild_id, select: s.data)

    Repo.one(query)
  end

  def create_server_settings(guild_id) do
    %ServerSettingsEntry{}
    |> ServerSettingsEntry.changeset(%{guild_id: guild_id, data: %{}})
    |> Repo.insert()
  end

  def edit_server_settings(guild_id, attrs) do
    settings = get_server_settings(guild_id)

    case settings do
      nil ->
        {:error, :not_found}

      settings ->
        data = ServerSettingsEntry.data_changeset(settings.data, attrs)

        settings
        |> ServerSettingsEntry.changeset()
        |> Ecto.Changeset.put_embed(:data, data)
        |> Repo.update()
    end
  end

  def delete_server_settings(guild_id) do
    query = from(s in ServerSettingsEntry, where: s.guild_id == ^guild_id)

    case Repo.delete_all(query) do
      {1, _} -> :ok
      _ -> :error
    end
  end

  def change_server_settings(%ServerSettingsEntry{} = server_settings_entry, attrs \\ %{}) do
    ServerSettingsEntry.changeset(server_settings_entry, attrs)
  end
end
