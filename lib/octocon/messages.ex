defmodule Octocon.Messages do
  @moduledoc """
  The Messages context.
  """

  import Ecto.Query, warn: false
  alias Octocon.MessageRepo, as: Repo

  alias Octocon.Messages.Message

  defp ensure_on_primary do
    unless Fly.RPC.is_primary?() do
      raise "This module should only accessed on the primary region."
    end
  end

  def insert_message(attrs) do
    ensure_on_primary()

    %Message{}
    |> Message.changeset(attrs)
    |> Repo.insert()
  end

  def lookup_message(message_id) do
    ensure_on_primary()

    message_timestamp = Nostrum.Snowflake.creation_time(message_id)

    query =
      from m in Message,
        where: m.message_id == ^to_string(message_id),
        where: m.timestamp == ^message_timestamp,
        limit: 1

    Repo.one(query)
  end
end
