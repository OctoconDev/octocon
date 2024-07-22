defmodule Octocon.Repo.Migrations.CreateFriendships do
  use Ecto.Migration

  def change do
    create table(:friendships, primary_key: false) do
      add :user_id, references(:users, type: :string, on_delete: :delete_all), primary_key: true
      add :friend_id, references(:users, type: :string, on_delete: :delete_all), primary_key: true
      add :level, :integer, default: 0
      add :since, :utc_datetime

      timestamps()
    end

    create index(:friendships, :user_id)
    create index(:friendships, :friend_id)
  end
end
