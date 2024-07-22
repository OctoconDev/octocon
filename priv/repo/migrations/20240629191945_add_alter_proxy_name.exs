defmodule Octocon.Repo.Local.Migrations.AddAlterProxyName do
  use Ecto.Migration

  def change do
    alter table(:alters) do
      add :proxy_name, :text, default: nil
    end

    alter table(:users) do
      add :ids_as_proxies, :boolean, default: false
    end
  end
end
