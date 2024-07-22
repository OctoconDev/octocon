defmodule Octocon.Repo.Migrations.AddCustomFields do
  use Ecto.Migration

  def change do
    alter table(:users) do
      add :fields, {:array, :map}, default: []
    end
  end
end
