defmodule Octocon.Repo.Local.Migrations.RefactorProxyCache do
  use Ecto.Migration

  def up do
    drop_if_exists table(:pc_items)

    create table(:proxy_cache_items, primary_key: false) do
      add :user_id,
          references(:users, type: :string, on_delete: :delete_all),
          size: 7,
          primary_key: true

      add :data, :binary
    end
  end

  def down do
    drop table(:proxy_cache_items)
  end
end
