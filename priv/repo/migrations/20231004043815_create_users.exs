defmodule Octocon.Repo.Migrations.CreateUsers do
  use Ecto.Migration

  def change do
    create table(:users, primary_key: false) do
      add :id, :string, primary_key: true, size: 7
      add :email, :text
      add :discord_id, :string, size: 22
      add :username, :string, size: 16
      add :avatar_url, :text

      add :primary_front, :integer
      add :lifetime_alter_count, :integer, default: 0

      # Discord
      add :autoproxy_mode, :integer
      add :last_proxy_id, :int2

      timestamps()
    end

    create unique_index(:users, [:email])
    create unique_index(:users, [:discord_id])
    create unique_index(:users, [:username])
  end
end
