defmodule Octocon.Repo.Migrations.CreateGlobalJournals do
  use Ecto.Migration

  def change do
    create table(:global_journals) do
      timestamps()
    end
  end
end
