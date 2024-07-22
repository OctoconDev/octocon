defmodule Octocon.Repo.Migrations.CreateTags do
  use Ecto.Migration

  def change do
    create table(:tags, primary_key: false) do
      add :id, :uuid, primary_key: true

      add :user_id,
          references(:users, type: :string, on_delete: :delete_all),
          size: 7

      add :name, :string
      add :color, :string, size: 7

      timestamps()
    end

    create index(:tags, [:user_id])

    create table(:alter_tags, primary_key: false) do
      add :user_id,
          references(:users, type: :string, on_delete: :delete_all),
          size: 7

      add :tag_id,
          references(:tags, type: :uuid, on_delete: :delete_all)

      add :alter_id,
          references(:alters, type: :int2, on_delete: :delete_all, with: [user_id: :user_id])
    end

    create index(:alter_tags, [:user_id])
    create unique_index(:alter_tags, [:tag_id, :alter_id])

    # Missed in test2
    create_if_not_exists index(:global_journals, [:user_id])
  end
end
