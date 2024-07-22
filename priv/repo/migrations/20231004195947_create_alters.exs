defmodule Octocon.Repo.Migrations.CreateAlters do
  use Ecto.Migration

  def change do
    create table(:alters, primary_key: false) do
      add :id, :int2, primary_key: true

      add :user_id, references(:users, type: :string, on_delete: :delete_all),
        primary_key: true,
        size: 7

      add :name, :text
      add :pronouns, :text
      add :description, :text
      add :security_level, :int2
      add :avatar_url, :text
      add :extra_images, {:array, :text}
      add :color, :string, size: 7

      # Discord
      add :discord_proxies, {:array, :string}

      add :fields, :map

      timestamps()
    end
  end
end
