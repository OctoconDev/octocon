defmodule Octocon.Repo.Migrations.ExtendJournalEntries do
  use Ecto.Migration

  def change do
    drop_if_exists table(:global_journal_alters)
    drop_if_exists table(:global_journals)
    drop_if_exists table(:alter_journals)

    create table(:global_journals, primary_key: false) do
      add :id, :uuid, primary_key: true

      add :user_id,
          references(:users, type: :string, on_delete: :delete_all),
          size: 7

      add :title, :text
      add :content, :text
      add :color, :string, size: 7

      timestamps()
    end

    create table(:global_journal_alters, primary_key: false) do
      add :user_id,
          references(:users, type: :string, on_delete: :delete_all),
          size: 7

      add :global_journal_id,
          references(:global_journals, type: :uuid, on_delete: :delete_all)

      add :alter_id,
          references(:alters, type: :int2, on_delete: :delete_all, with: [user_id: :user_id])
    end

    create index(:global_journal_alters, [:user_id])
    create unique_index(:global_journal_alters, [:global_journal_id, :alter_id])

    create table(:alter_journals, primary_key: false) do
      add :id, :uuid, primary_key: true

      add :user_id,
          references(:users, type: :string, on_delete: :delete_all),
          size: 7

      add :alter_id,
          references(:alters, type: :int2, on_delete: :delete_all, with: [user_id: :user_id])

      add :title, :text
      add :content, :text, default: ""
      add :color, :string, size: 7

      timestamps()
    end
  end
end
