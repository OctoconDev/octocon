defmodule Octocon.Repo.Local.Migrations.AddCaseInsensitiveProxying do
  use Ecto.Migration

  def change do
    alter table(:users) do
      add :case_insensitive_proxying, :boolean, default: false
    end
  end
end
