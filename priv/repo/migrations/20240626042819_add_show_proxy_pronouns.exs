defmodule Octocon.Repo.Local.Migrations.AddShowProxyPronouns do
  use Ecto.Migration

  def change do
    alter table(:users) do
      add :show_proxy_pronouns, :boolean, default: true
    end
  end
end
