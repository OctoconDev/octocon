defmodule OctoconWeb.System.FrontingController do
  use OctoconWeb, :controller

  alias Octocon.{
    Accounts,
    Fronts
  }

  import OctoconWeb.Utils.Systems

  action_fallback OctoconWeb.FallbackController

  def index(conn, %{"system_id" => system_id}) do
    caller_id = conn.private[:guardian_default_resource]

    case parse_system(conn, system_id) do
      {:noreply, conn} ->
        conn

      {:self, system} ->
        render(conn, :index_me, fronts: Fronts.currently_fronting({:system, system.id}))

      {:other, system} ->
        render(conn, :index,
          fronts: Fronts.currently_fronting_guarded({:system, system.id}, {:system, caller_id})
        )
    end
  end

  def show(conn, %{"id" => id}) do
    caller_id = conn.private[:guardian_default_resource]

    try do
      case Fronts.get_by_id({:system, caller_id}, {:id, id}) do
        nil ->
          conn
          |> put_status(:not_found)
          |> json(%{error: "Front not found.", code: "front_not_found"})

        front ->
          render(conn, :show_me, front: front)
      end
    rescue
      _ in Ecto.Query.CastError ->
        conn
        |> put_status(:bad_request)
        |> json(%{error: "Invalid front ID.", code: "invalid_front_id"})
    end
  end

  def delete(conn, %{"id" => id}) do
    caller_id = conn.private[:guardian_default_resource]

    case Fronts.delete_front({:system, caller_id}, id) do
      {:ok, _front} ->
        send_resp(conn, :no_content, "")

      {:error, _changeset} ->
        conn
        |> put_status(:bad_request)
        |> json(%{error: "Invalid front ID.", code: "invalid_id"})

      _ ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end

  def between(conn, %{"start" => time_start_unix, "end" => time_end_unix})
      when is_integer(time_start_unix) and is_integer(time_end_unix) do
    system_id = conn.private[:guardian_default_resource]

    with {:ok, time_start} <- DateTime.from_unix(time_start_unix),
         {:ok, time_end} <- DateTime.from_unix(time_end_unix) do
      render(conn, :index_me,
        fronts: Fronts.fronted_between({:system, system_id}, time_start, time_end)
      )
    else
      {:error, _} ->
        conn
        |> put_status(:bad_request)
        |> json(%{
          error: "Invalid start or end anchor. Please pass valid Unix timestamps.",
          code: "invalid_anchor"
        })
    end
  end

  def between(conn, %{"start" => time_start_unix, "end" => time_end_unix})
      when is_binary(time_start_unix) and is_binary(time_end_unix) do
    with {time_start, _} <- Integer.parse(time_start_unix),
         {time_end, _} <- Integer.parse(time_end_unix) do
      between(conn, %{"start" => time_start, "end" => time_end})
    else
      _ ->
        conn
        |> put_status(:bad_request)
        |> json(%{
          error: "Invalid start or end anchor. Please pass valid Unix timestamps.",
          code: "invalid_anchor"
        })
    end
  end

  def month(conn, %{"end_anchor" => end_anchor}) when is_integer(end_anchor) do
    system_id = conn.private[:guardian_default_resource]

    case DateTime.from_unix(end_anchor) do
      {:ok, datetime} ->
        render(conn, :index_me, fronts: Fronts.fronted_for_month({:system, system_id}, datetime))

      {:error, _} ->
        conn
        |> put_status(:bad_request)
        |> json(%{
          error: "Invalid end anchor. Please pass a valid Unix timestamp.",
          code: "invalid_end_anchor"
        })
    end
  end

  def month(conn, %{"end_anchor" => end_anchor}) when is_binary(end_anchor) do
    case Integer.parse(end_anchor) do
      {new_anchor, _} ->
        month(conn, %{"end_anchor" => new_anchor})

      _ ->
        conn
        |> put_status(:bad_request)
        |> json(%{
          error: "Invalid end anchor. Please pass a valid Unix timestamp.",
          code: "invalid_end_anchor"
        })
    end
  end

  def update(conn, %{"start" => start_fronts, "end" => end_fronts})
      when is_list(start_fronts) and is_list(end_fronts) do
    system_id = conn.private[:guardian_default_resource]

    case Fronts.bulk_update_fronts({:system, system_id}, start_fronts, end_fronts) do
      {:ok, _} ->
        send_resp(conn, :no_content, "")

      {:error, _} ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end

  def primary(conn, params) do
    system_id = conn.private[:guardian_default_resource]
    alter_id = params["id"] || nil

    cond do
      is_nil(alter_id) ->
        Accounts.set_primary_front({:system, system_id}, nil)
        send_resp(conn, :no_content, "")

      is_number(alter_id) and alter_id not in 1..32_767 ->
        conn
        |> put_status(:bad_request)
        |> json(%{error: "Invalid alter ID.", code: "invalid_alter_id"})

      is_number(alter_id) ->
        if Fronts.is_fronting?({:system, system_id}, {:id, alter_id}) do
          Accounts.set_primary_front({:system, system_id}, alter_id)
          send_resp(conn, :no_content, "")
        else
          conn
          |> put_status(:bad_request)
          |> json(%{error: "That alter is not currently fronting.", code: "not_fronting"})
        end

      true ->
        conn
        |> put_status(:bad_request)
        |> json(%{error: "Invalid alter ID.", code: "invalid_alter_id"})
    end
  end

  def update_comment(conn, %{"id" => front_id, "comment" => comment}) do
    system_id = conn.private[:guardian_default_resource]

    case Fronts.update_comment({:system, system_id}, front_id, comment) do
      :ok ->
        send_resp(conn, :no_content, "")

      {:error, :no_front} ->
        conn
        |> put_status(:bad_request)
        |> json(%{error: "A front does not exist with that ID.", code: "no_front"})

      {:error, :changeset} ->
        conn
        |> put_status(:bad_request)
        |> json(%{
          error: "Invalid comment. Must be less than 50 characters.",
          code: "invalid_comment"
        })
    end
  end

  def start(conn, %{"id" => alter_id} = params) do
    system_id = conn.private[:guardian_default_resource]

    comment = params["comment"] || ""

    case Fronts.start_front({:system, system_id}, {:id, alter_id}, comment) do
      {:ok, front} ->
        conn
        |> put_status(:created)
        |> put_resp_header("location", ~p"/api/systems/me/front/#{front.id}")
        |> text("")

      {:error, :already_fronting} ->
        conn
        |> put_status(:bad_request)
        |> json(%{error: "That alter is already fronting.", code: "already_fronting"})

      {:error, _} ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end

  def endd(conn, %{"id" => alter_id}) do
    system_id = conn.private[:guardian_default_resource]

    case Fronts.end_front({:system, system_id}, {:id, alter_id}) do
      :ok ->
        send_resp(conn, :no_content, "")

      {:error, :not_fronting} ->
        conn
        |> put_status(:bad_request)
        |> json(%{error: "That alter is not currently fronting.", code: "not_fronting"})

      _ ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end

  def set(conn, %{"id" => id}) do
    system_id = conn.private[:guardian_default_resource]

    case Fronts.set_front({:system, system_id}, {:id, id}) do
      {:ok, _} ->
        send_resp(conn, :no_content, "")

      {:error, :already_fronting} ->
        conn
        |> put_status(:bad_request)
        |> json(%{error: "That alter is already fronting.", code: "already_fronting"})

      _ ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end
end
