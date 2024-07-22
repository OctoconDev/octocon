defmodule Octocon.Repo.Migrations.Test1 do
  use Ecto.Migration

  def change do
    drop_if_exists table(:global_journal_alters)
    drop_if_exists table(:global_journals)
    drop_if_exists table(:alter_journals)
  end
end
