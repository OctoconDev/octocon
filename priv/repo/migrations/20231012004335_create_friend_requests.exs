defmodule Octocon.Repo.Migrations.CreateFriendRequests do
  use Ecto.Migration

  def change do
    create table(:friend_requests, primary_key: false) do
      add :from_id, references(:users, type: :string, on_delete: :delete_all), primary_key: true
      add :to_id, references(:users, type: :string, on_delete: :delete_all), primary_key: true
      add :date_sent, :utc_datetime

      timestamps()
    end

    create index(:friend_requests, :from_id)
    create index(:friend_requests, :to_id)
  end
end
