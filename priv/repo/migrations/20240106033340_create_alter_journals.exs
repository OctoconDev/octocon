defmodule Octocon.Repo.Migrations.CreateAlterJournals do
  use Ecto.Migration

  def change do
    create table(:alter_journals) do
      timestamps()
    end
  end
end
