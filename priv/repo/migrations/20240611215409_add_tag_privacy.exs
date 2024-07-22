defmodule Octocon.Repo.Migrations.AddTagPrivacy do
  use Ecto.Migration

  def change do
    alter table(:tags) do
      add :security_level, :int2, default: 3
    end
  end
end
