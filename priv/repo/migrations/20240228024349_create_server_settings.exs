defmodule Octocon.Repo.Migrations.CreateServerSettings do
  use Ecto.Migration

  def change do
    create table(:server_settings, primary_key: false) do
      add :guild_id, :string, primary_key: true, size: 22
      add :data, :map
      timestamps()
    end
  end
end
