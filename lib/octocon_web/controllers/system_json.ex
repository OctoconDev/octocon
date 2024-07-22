defmodule OctoconWeb.SystemJSON do
  def show_me(%{system: system}) do
    %{data: data_me(system)}
  end

  def show(%{system: system}) do
    %{data: data(system)}
  end

  def data_me(system) do
    map =
      system
      |> Map.from_struct()
      |> Map.drop([:__meta__, :alters, :fronts, :global_journals, :inserted_at, :updated_at])

    %{map | fields: map.fields |> Enum.map(&Map.drop(&1, [:__struct__, :__meta__]))}
  end

  defp data(nil), do: nil

  defp data(%{
         id: id,
         avatar_url: avatar_url,
         username: username,
         description: description,
         friend_status: friend_status
       }) do
    %{
      id: id,
      avatar_url: avatar_url,
      username: username,
      description: description,
      friend_status: friend_status
    }
  end

  defp data(%{
         id: id,
         avatar_url: avatar_url,
         username: username,
         description: description
       }) do
    %{
      id: id,
      avatar_url: avatar_url,
      username: username,
      description: description
    }
  end
end
