defmodule Octocon.Repo.Local.Migrations.AddTagDescriptions do
  use Ecto.Migration

  def change do
    alter table(:tags) do
      add :description, :string, default: nil
    end
  end
end
