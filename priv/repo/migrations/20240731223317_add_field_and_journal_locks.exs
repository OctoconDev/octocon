defmodule Octocon.Repo.Local.Migrations.AddFieldAndJournalLocks do
  use Ecto.Migration

  def change do
    alter table(:global_journals) do
      add :locked, :boolean, default: false
    end

    alter table(:alter_journals) do
      add :locked, :boolean, default: false
    end
  end
end
