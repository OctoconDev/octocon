defmodule OctoconWeb.FriendRequestController do
  use OctoconWeb, :controller

  import OctoconWeb.Utils.Systems

  alias Octocon.Friendships

  action_fallback OctoconWeb.FallbackController

  def index(conn, _) do
    system_id = conn.private[:guardian_default_resource]
    incoming = Friendships.incoming_friend_requests({:system, system_id})
    outgoing = Friendships.outgoing_friend_requests({:system, system_id})

    render(conn, :index, requests: %{incoming: incoming, outgoing: outgoing})
  end

  def accept(conn, %{"id" => id}) do
    caller_id = conn.private[:guardian_default_resource]

    case parse_system(conn, id) do
      {:noreply, conn} ->
        conn

      {:self, _} ->
        conn
        |> put_status(:bad_request)
        |> json(%{
          error: "You cannot accept a friend request from yourself.",
          code: "cannot_accept_self"
        })

      {:other, system} ->
        case Friendships.accept_request({:system, system.id}, {:system, caller_id}) do
          :ok ->
            send_resp(conn, :no_content, "")

          {:error, :already_friends} ->
            conn
            |> put_status(:bad_request)
            |> json(%{error: "You are already friends with this system.", code: "already_friends"})

          {:error, :not_requested} ->
            conn
            |> put_status(:not_found)
            |> json(%{
              error: "No friend request found from system \"#{system.id}\".",
              code: "not_requested"
            })

          _ ->
            conn
            |> put_status(:internal_server_error)
            |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
        end
    end
  end

  def reject(conn, %{"id" => id}) do
    caller_id = conn.private[:guardian_default_resource]

    case parse_system(conn, id) do
      {:noreply, conn} ->
        conn

      {:self, _} ->
        conn
        |> put_status(:bad_request)
        |> json(%{
          error: "You cannot reject a friend request from yourself.",
          code: "cannot_reject_self"
        })

      {:other, system} ->
        case Friendships.reject_request({:system, system.id}, {:system, caller_id}) do
          :ok ->
            send_resp(conn, :no_content, "")

          {:error, :already_friends} ->
            conn
            |> put_status(:bad_request)
            |> json(%{error: "You are already friends with this system.", code: "already_friends"})

          {:error, :not_requested} ->
            conn
            |> put_status(:not_found)
            |> json(%{
              error: "No friend request found from system \"#{system.id}\".",
              code: "not_requested"
            })

          _ ->
            conn
            |> put_status(:internal_server_error)
            |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
        end
    end
  end

  def cancel(conn, %{"id" => id}) do
    caller_id = conn.private[:guardian_default_resource]

    case parse_system(conn, id) do
      {:noreply, conn} ->
        conn

      {:self, _} ->
        conn
        |> put_status(:bad_request)
        |> json(%{
          error: "You cannot cancel a friend request to yourself.",
          code: "cannot_cancel_self"
        })

      {:other, system} ->
        case Friendships.cancel_request({:system, caller_id}, {:system, system.id}) do
          :ok ->
            send_resp(conn, :no_content, "")

          {:error, :already_friends} ->
            conn
            |> put_status(:bad_request)
            |> json(%{error: "You are already friends with this system.", code: "already_friends"})

          {:error, :not_requested} ->
            conn
            |> put_status(:not_found)
            |> json(%{
              error: "No friend request found to system \"#{system.id}\".",
              code: "not_requested"
            })

          _ ->
            conn
            |> put_status(:internal_server_error)
            |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
        end
    end
  end

  def send(conn, %{"id" => id}) do
    caller_id = conn.private[:guardian_default_resource]

    case parse_system(conn, id) do
      {:noreply, conn} ->
        conn

      {:self, _} ->
        conn
        |> put_status(:bad_request)
        |> json(%{
          error: "You cannot send a friend request to yourself.",
          code: "cannot_send_self"
        })

      {:other, system} ->
        case Friendships.send_request({:system, caller_id}, {:system, system.id}) do
          {:ok, :accepted} ->
            send_resp(conn, :no_content, "")

          {:ok, :sent} ->
            send_resp(conn, :no_content, "")

          {:error, :already_friends} ->
            conn
            |> put_status(:bad_request)
            |> json(%{error: "You are already friends with this system.", code: "already_friends"})

          {:error, :already_sent_request} ->
            conn
            |> put_status(:bad_request)
            |> json(%{
              error: "You have already sent a friend request to this system.",
              code: "already_sent_request"
            })

          _ ->
            conn
            |> put_status(:internal_server_error)
            |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
        end
    end
  end
end
