defmodule OctoconWeb.FriendController do
  use OctoconWeb, :controller

  import OctoconWeb.Utils.Systems

  alias Octocon.Friendships

  action_fallback OctoconWeb.FallbackController

  def index(conn, _) do
    system_id = conn.private[:guardian_default_resource]
    friendships = Friendships.list_friendships_guarded({:system, system_id})

    render(conn, :index, friendships: friendships)
  end

  def show(conn, %{"id" => id}) do
    caller_id = conn.private[:guardian_default_resource]

    case parse_system(conn, id) do
      {:noreply, conn} ->
        conn

      {:self, _system} ->
        conn
        |> put_status(:bad_request)
        |> json(%{
          error:
            "I'm pretty sure you don't count as your own friend. (Cannot view friendship status for self.)",
          code: "cannot_view_own_friendship"
        })

      {:other, system} ->
        case Friendships.get_friendship_guarded({:system, caller_id}, {:system, system.id}) do
          nil ->
            conn
            |> put_status(:not_found)
            |> json(%{
              error: "You are not friends with that system.",
              code: "friendship_not_found"
            })

          friendship ->
            render(conn, :show, friendship: friendship)
        end
    end
  end

  def delete(conn, %{"id" => id}) do
    caller_id = conn.private[:guardian_default_resource]

    case parse_system(conn, id) do
      {:noreply, conn} ->
        conn

      {:self, _system} ->
        conn
        |> put_status(:bad_request)
        |> json(%{
          error:
            "I'm pretty sure you don't count as your own friend. (Cannot delete friendship with self.)",
          code: "cannot_delete_own_friendship"
        })

      {:other, system} ->
        case Friendships.remove_friendship({:system, caller_id}, {:system, system.id}) do
          :ok ->
            send_resp(conn, :no_content, "")

          {:error, :not_friends} ->
            conn
            |> put_status(:not_found)
            |> json(%{
              error: "You are not friends with that system.",
              code: "friendship_not_found"
            })

          {:error, :database} ->
            conn
            |> put_status(:internal_server_error)
            |> json(%{
              error: "An unknown error occurred.",
              code: "unknown_error"
            })
        end
    end
  end

  def trust(conn, %{"id" => id}) do
    caller_id = conn.private[:guardian_default_resource]

    case parse_system(conn, id) do
      {:noreply, conn} ->
        conn

      {:self, _system} ->
        conn
        |> put_status(:bad_request)
        |> json(%{
          error: "I'm pretty sure you don't count as your own friend. (Cannot trust self.)",
          code: "cannot_trust_self"
        })

      {:other, system} ->
        case Friendships.trust_friend({:system, caller_id}, {:system, system.id}) do
          :ok ->
            send_resp(conn, :no_content, "")

          {:error, :not_friends} ->
            conn
            |> put_status(:not_found)
            |> json(%{
              error: "You are not friends with that system.",
              code: "friendship_not_found"
            })

          {:error, :database} ->
            conn
            |> put_status(:internal_server_error)
            |> json(%{
              error: "An unknown error occurred.",
              code: "unknown_error"
            })
        end
    end
  end

  def untrust(conn, %{"id" => id}) do
    caller_id = conn.private[:guardian_default_resource]

    case parse_system(conn, id) do
      {:noreply, conn} ->
        conn

      {:self, _system} ->
        conn
        |> put_status(:bad_request)
        |> json(%{
          error: "I'm pretty sure you don't count as your own friend. (Cannot untrust self.)",
          code: "cannot_untrust_self"
        })

      {:other, system} ->
        case Friendships.untrust_friend({:system, caller_id}, {:system, system.id}) do
          :ok ->
            send_resp(conn, :no_content, "")

          {:error, :not_friends} ->
            conn
            |> put_status(:not_found)
            |> json(%{
              error: "You are not friends with that system.",
              code: "friendship_not_found"
            })

          {:error, :database} ->
            conn
            |> put_status(:internal_server_error)
            |> json(%{
              error: "An unknown error occurred.",
              code: "unknown_error"
            })
        end
    end
  end
end
