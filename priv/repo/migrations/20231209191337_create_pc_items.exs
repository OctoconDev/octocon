defmodule Octocon.Repo.Migrations.CreatePcItems do
  use Ecto.Migration

  def change do
    create table(:pc_items, primary_key: false) do
      add :id, :int2, primary_key: true, default: 1
      add :data, :binary
      timestamps()
    end

    create constraint(:pc_items, :id, check: "id = 1")
  end
end
