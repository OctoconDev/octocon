defmodule OctoconWeb.System.AlterJSON do
  alias Octocon.Alters.Alter

  def index_me(%{alters: alters}) do
    %{data: Enum.map(alters, &data_me/1)}
  end

  def index(%{alters: alters}) do
    %{data: alters |> Enum.map(&data/1)}
  end

  def show_me(%{alter: alter}) do
    %{data: data_me(alter)}
  end

  def show(%{alter: alter}) do
    %{data: data(alter)}
  end

  def data_me(alter) do
    # Only strip internal metadata
    alter
    |> Map.from_struct()
    |> Map.drop(Alter.dropped_fields())
    |> Map.put(:fields, alter.fields |> Enum.map(&Map.drop(&1, [:__struct__, :__meta__])))
  end

  def data(%Alter{} = alter) do
    Map.take(alter, [
      :id,
      :name,
      :pronouns,
      :description,
      :fields,
      :avatar_url,
      :extra_images,
      :color
    ])
    |> Map.put(:fields, alter.fields |> Enum.map(&Map.drop(&1, [:__struct__, :__meta__])))
  end
end
