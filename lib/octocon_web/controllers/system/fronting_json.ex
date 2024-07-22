defmodule OctoconWeb.System.FrontingJSON do
  @moduledoc false

  alias Octocon.Fronts.Front

  def index_me(%{fronts: fronts}) do
    %{data: Enum.map(fronts, &data_me/1)}
  end

  def index(%{fronts: fronts}) do
    %{data: Enum.map(fronts, &data/1)}
  end

  def show_me(%{front: front}) do
    %{data: data_me(front)}
  end

  def data_me(%Front{} = front) do
    front
    |> Map.from_struct()
    |> Map.drop([:__meta__, :alter, :user])
  end

  def data_me(%{alter: alter, front: front, primary: primary}) do
    %{
      front:
        front
        |> Map.from_struct()
        |> Map.drop([:__meta__, :alter, :user]),
      alter:
        alter
        |> Map.from_struct()
        |> Map.take([:id, :name, :pronouns, :color, :avatar_url]),
      primary: primary
    }
  end

  def data(%{alter: alter, front: front, primary: primary}) do
    %{
      front:
        front
        |> Map.take([:alter_id, :comment]),
      alter:
        alter
        |> Map.take([:id, :name, :pronouns, :color, :avatar_url]),
      primary: primary
    }
  end
end
