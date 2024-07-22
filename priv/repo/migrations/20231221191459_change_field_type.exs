defmodule Octocon.Repo.Migrations.ChangeFieldType do
  use Ecto.Migration

  def change do
    alter table(:alters) do
      remove :fields
      add :fields, {:array, :map}, default: []
    end
  end
end
