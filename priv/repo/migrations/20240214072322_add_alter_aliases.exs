defmodule Octocon.Repo.Migrations.AddAlterAliases do
  use Ecto.Migration

  def change do
    alter table(:alters) do
      add :alias, :text, default: nil
    end

    create unique_index(:alters, [:user_id, :alias])
  end
end
