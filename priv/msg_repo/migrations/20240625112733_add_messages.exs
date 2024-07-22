defmodule Octocon.MessageRepo.Migrations.AddMessages do
  use Ecto.Migration

  import Timescale.Migration

  def up do
    execute("CREATE EXTENSION IF NOT EXISTS timescaledb CASCADE")

    create table(:messages, primary_key: false) do
      add :timestamp, :utc_datetime, null: false
      add :message_id, :string, size: 22, null: false
      add :author_id, :string, size: 22, null: false
      add :system_id, :string, size: 7, null: false
      add :alter_id, :int2, null: false
    end

    execute(
      "SELECT create_hypertable('messages', 'timestamp', chunk_time_interval => INTERVAL '1 week');"
    )

    execute(
      "ALTER TABLE messages SET (timescaledb.compress, timescaledb.compress_segmentby = 'author_id');"
    )

    execute("SELECT add_compression_policy('messages', INTERVAL '1 week');")
    execute("SELECT add_retention_policy('messages', INTERVAL '3 months');")
  end

  def down do
    drop_if_exists table(:messages)

    execute("DROP EXTENSION IF EXISTS timescaledb CASCADE")
  end
end
