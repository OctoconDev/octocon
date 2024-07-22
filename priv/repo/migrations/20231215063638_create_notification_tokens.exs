defmodule Octocon.Repo.Migrations.CreateNotificationTokens do
  use Ecto.Migration

  def change do
    create table(:notification_tokens, primary_key: false) do
      add :user_id, references(:users, type: :string, on_delete: :delete_all), primary_key: true
      add :token, :string, primary_key: true

      timestamps()
    end

    create index(:notification_tokens, [:user_id])
  end
end
