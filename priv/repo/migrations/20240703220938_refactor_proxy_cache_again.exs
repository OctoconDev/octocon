defmodule Octocon.Repo.Local.Migrations.RefactorProxyCacheAgain do
  use Ecto.Migration

  def change do
    drop_if_exists table(:proxy_cache_items)

    create table(:proxy_cache_items, primary_key: false) do
      add :discord_id, :string, primary_key: true
      add :data, :binary
    end
  end
end
