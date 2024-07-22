defmodule Octocon.Repo.Migrations.CreateChannelBlacklists do
  use Ecto.Migration

  def change do
    create table(:channel_blacklists, primary_key: false) do
      add :guild_id, :string, size: 22
      add :channel_id, :string, primary_key: true, size: 22

      timestamps()
    end

    create index(:channel_blacklists, :guild_id)
  end
end
