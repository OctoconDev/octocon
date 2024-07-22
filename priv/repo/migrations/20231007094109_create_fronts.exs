defmodule Octocon.Repo.Migrations.CreateFronts do
  use Ecto.Migration

  def change do
    create table(:fronts, primary_key: false) do
      add :id, :uuid, primary_key: true

      add :user_id,
          references(:users, type: :string, on_delete: :delete_all),
          size: 7

      add :alter_id,
          references(:alters, type: :int2, on_delete: :delete_all, with: [user_id: :user_id])

      add :comment, :string, size: 50, default: ""
      add :time_start, :utc_datetime
      add :time_end, :utc_datetime
    end

    create index(:fronts, [:user_id, :alter_id])

    create index(:fronts, :user_id,
             where: "time_end IS NULL",
             name: :currently_fronting_by_user_id
           )

    create index(:fronts, :user_id)
  end
end
