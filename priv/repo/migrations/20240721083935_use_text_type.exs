defmodule Octocon.Repo.Local.Migrations.UseTextType do
  use Ecto.Migration

  def change do
    alter table(:users) do
      modify :description, :text
      modify :discord_id, :text
      modify :username, :text
      modify :system_tag, :text
    end

    alter table(:tags) do
      modify :description, :text
      modify :name, :text
    end
    
    alter table(:alters) do
      modify :discord_proxies, {:array, :text}
    end

    alter table(:fronts) do
      modify :comment, :text
    end

    alter table(:proxy_cache_items) do
      modify :discord_id, :text
    end

    alter table(:notification_tokens) do
      modify :token, :text
    end

    alter table(:channel_blacklists) do
      modify :channel_id, :text
      modify :guild_id, :text
    end
  end
end
