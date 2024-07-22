defmodule Octocon.Repo.Local.Migrations.AddParentTag do
  use Ecto.Migration

  def change do
    alter table(:tags) do
      add :parent_tag_id, references(:tags, on_delete: :nilify_all, type: :uuid), default: nil
    end
  end
end
