defmodule Octocon.Repo.Migrations.AddSystemTag do
  use Ecto.Migration

  def change do
    alter table(:users) do
      add :system_tag, :string, default: nil
      add :show_system_tag, :boolean, default: false
    end
  end
end
