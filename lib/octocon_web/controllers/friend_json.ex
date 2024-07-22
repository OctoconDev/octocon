defmodule OctoconWeb.FriendJSON do
  def index(%{friendships: friendships}) do
    %{data: Enum.map(friendships, &data/1)}
  end

  def show(%{friendship: friendship}) do
    %{data: data(friendship)}
  end

  def data(%{friend: friend, friendship: friendship, fronting: fronting}) do
    %{
      friendship: %{
        level: friendship.level,
        since: friendship.since
      },
      friend: %{
        id: friend.id,
        avatar_url: friend.avatar_url,
        username: friend.username,
        description: friend.description,
        discord_id: friend.discord_id
      },
      fronting:
        Enum.map(fronting, fn %{alter: alter, front: front, primary: primary} ->
          %{
            alter: OctoconWeb.System.AlterJSON.data(alter),
            front: Map.take(front, [:comment, :alter_id]),
            primary: primary
          }
        end)
    }
  end
end
