defmodule Octocon.Repo.Local.Migrations.AddSystemDescriptions do
  use Ecto.Migration

  def change do
    alter table(:users) do
      add :description, :string, default: nil
    end
  end
end
