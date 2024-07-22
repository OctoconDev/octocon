defmodule Octocon.Repo.Local.Migrations.AddLatchedAlter do
  use Ecto.Migration

  def change do
    alter table(:users) do
      add :latched_alter,
          references(:alters,
            type: :int2,
            column: :id,
            on_delete: :nilify_all,
            with: [id: :user_id]
          ),
          default: nil
    end
  end
end
