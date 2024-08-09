defmodule Octocon.Repo.Local.Migrations.AddJournalEntryPins do
  use Ecto.Migration

  def change do
    alter table(:global_journals) do
      add :pinned, :boolean, default: false
    end

    alter table(:alter_journals) do
      add :pinned, :boolean, default: false
    end
  end
end
