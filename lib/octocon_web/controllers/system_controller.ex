defmodule OctoconWeb.SystemController do
  use OctoconWeb, :controller

  import OctoconWeb.Utils.Systems

  alias Octocon.{
    Alters,
    Friendships,
    Tags
  }

  alias OctoconWeb.System.{
    TagJSON,
    AlterJSON
  }

  alias OctoconWeb.FriendJSON

  action_fallback OctoconWeb.FallbackController

  def show(conn, %{"system_id" => id}) do
    case parse_system(conn, id) do
      {:noreply, conn} -> conn
      {:self, system} -> render(conn, :show_me, system: system)
      {:other, system} -> render(conn, :show, system: system)
    end
  end

  def batch(conn, %{"system_id" => id}) do
    caller_id = conn.private[:guardian_default_resource]

    case parse_system(conn, id) do
      {:noreply, conn} -> conn
      {:self, _system} ->
        conn
        |> put_status(:forbidden)
        |> json(%{error: "You cannot view your own system through this endpoint.", code: "invalid_endpoint"})
      {:other, system} -> 
        friendship = Task.async(fn -> Friendships.get_friendship_guarded({:system, caller_id}, {:system, system.id}) end)
        alters = Task.async(fn -> Alters.get_alters_guarded({:system, system.id}, {:system, caller_id}) end)
        tags = Task.async(fn -> Tags.get_tags_guarded({:system, system.id}, {:system, caller_id}) end)

        [friendship, tags, alters]
        |> Task.await_many()
        |> then(fn [friendship, tags, alters] ->
          [
            FriendJSON.data(friendship),
            Enum.map(tags, &TagJSON.data/1),
            Enum.map(alters, &AlterJSON.data/1)
          ]
        end)
        |> then(fn [friendship, tags, alters] ->
          conn
          |> render(
            :batch,
            friendship: friendship,
            tags: tags,
            alters: alters
          )
        end)
    end
  end

  # def update(conn, %{"id" => id, "alter" => alter_params}) do
  #  alter = Alters.get_alter!(id)
  #
  #  with {:ok, %Alter{} = alter} <- Alters.update_alter(alter, alter_params) do
  #    render(conn, :show, alter: alter)
  #  end
  # end

  # def delete(conn, %{"id" => id}) do
  #  alter = Alters.get_alter!(id)
  #
  #  with {:ok, %Alter{}} <- Alters.delete_alter(alter) do
  #    send_resp(conn, :no_content, "")
  #  end
  # end
end
