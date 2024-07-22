defmodule OctoconWeb.System.TagJSON do
  def index_me(%{tags: tags}) do
    %{data: Enum.map(tags, &data_me/1)}
  end

  def index(%{tags: tags}) do
    %{data: Enum.map(tags, &data/1)}
  end

  def show_me(%{tag: tag}) do
    %{data: data_me(tag)}
  end

  def show(%{tag: tag}) do
    %{data: data(tag)}
  end

  def data_me(tag) do
    tag
    |> Map.from_struct()
    |> Map.drop([:__meta__, :user, :parent_tag])
  end

  def data(tag) when tag.alters == [] or tag.alters == nil do
    tag
    |> Map.from_struct()
    |> Map.take([:id, :name, :alters, :description, :user_id, :color, :parent_tag_id])
    |> Map.update!(:alters, fn _ -> [] end)
  end

  def data(tag) do
    tag
    |> Map.from_struct()
    |> Map.take([:id, :name, :alters, :description, :user_id, :color, :parent_tag_id])
    |> Map.update!(
      :alters,
      fn alters ->
        alters
        |> Enum.map(&Map.from_struct/1)
        |> Enum.map(&Map.take(&1, [:id, :name, :color, :avatar_url, :pronouns]))
      end
    )
  end
end
