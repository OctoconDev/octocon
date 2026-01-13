defmodule Octocon.Repo.Migrations.CreateRoleLocks do
  use Ecto.Migration

  def change do
    create table(:role_locks, primary_key: false) do
      add :guild_id, :string, primary_key: true
      add :role_id, :string, null: false

      timestamps()
    end
  end
end
