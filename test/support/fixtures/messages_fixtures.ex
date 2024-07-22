defmodule Octocon.MessagesFixtures do
  @moduledoc """
  This module defines test helpers for creating
  entities via the `Octocon.Messages` context.
  """

  @doc """
  Generate a message.
  """
  def message_fixture(attrs \\ %{}) do
    {:ok, message} =
      attrs
      |> Enum.into(%{})
      |> Octocon.Messages.create_message()

    message
  end
end
