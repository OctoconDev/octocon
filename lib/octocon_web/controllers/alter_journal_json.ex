defmodule OctoconWeb.AlterJournalJSON do
  def index(%{entries: entries}) do
    %{data: Enum.map(entries, &data/1)}
  end

  def show(%{entry: entry}) do
    %{data: data(entry)}
  end

  def data(entry) do
    entry
    |> Map.from_struct()
    |> Map.drop([:__meta__, :alter, :user])
  end
end
