defmodule OctoconWeb.System.AlterController do
  use OctoconWeb, :controller

  import OctoconWeb.Utils.Systems

  alias Octocon.Utils.Alter, as: AlterUtils
  alias Octocon.Alters

  action_fallback OctoconWeb.FallbackController

  def index(conn, %{"system_id" => system_id}) do
    caller_id = conn.private[:guardian_default_resource]

    case parse_system(conn, system_id) do
      {:noreply, conn} ->
        conn

      {:self, system} ->
        render(conn, :index_me, alters: Alters.get_alters_by_id({:system, system.id}))

      {:other, system} ->
        render(conn, :index,
          alters: Alters.get_alters_guarded({:system, system.id}, {:system, caller_id})
        )
    end
  end

  def show(conn, %{"system_id" => system_id, "id" => id}) do
    caller_id = conn.private[:guardian_default_resource]

    case parse_system(conn, system_id) do
      {:noreply, conn} ->
        conn

      {:self, _system} ->
        case Alters.get_alter_by_id({:system, caller_id}, {:id, id}) do
          {:ok, alter} ->
            render(conn, :show_me, alter: alter)

          _ ->
            conn
            |> put_status(:not_found)
            |> json(%{error: "Alter not found.", code: "alter_not_found"})
        end

      {:other, system} ->
        case Alters.get_alter_guarded({:system, system.id}, {:id, id}, {:system, caller_id}) do
          {:ok, alter} ->
            render(conn, :show, alter: alter)

          _ ->
            conn
            |> put_status(:not_found)
            |> json(%{error: "Alter not found.", code: "alter_not_found"})
        end
    end
  end

  def create(conn, %{"name" => alter_name}) do
    system_id = conn.private[:guardian_default_resource]

    case Alters.create_alter({:system, system_id}, %{name: alter_name}) do
      {:ok, id, alter} ->
        conn
        |> put_status(:created)
        |> put_resp_header("location", ~p"/api/systems/me/alters/#{id}")
        |> render(:show_me, alter: alter)

      {:error, :database} ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end

  def delete(conn, %{"id" => id}) do
    system_id = conn.private[:guardian_default_resource]

    case Alters.delete_alter({:system, system_id}, {:id, id}) do
      :ok ->
        send_resp(conn, :no_content, "")

      {:error, :no_alter} ->
        conn
        |> put_status(:not_found)
        |> json(%{error: "Alter not found.", code: "alter_not_found"})

      _ ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end

  def update(conn, %{"id" => id} = attrs) do
    system_id = conn.private[:guardian_default_resource]

    attrs =
      Map.take(attrs, [
        "name",
        "description",
        "avatar_url",
        "color",
        "pronouns",
        "security_level",
        "fields",
        "proxy_name",
        "alias"
      ])
      |> Enum.map(fn {k, v} -> {String.to_existing_atom(k), v} end)
      |> Map.new()

    if map_size(attrs) == 0 do
      conn
      |> put_status(:bad_request)
      |> json(%{error: "No valid alter attributes provided.", code: "no_alter_attributes"})
    else
      case Alters.update_alter({:system, system_id}, {:id, id}, attrs) do
        :ok ->
          send_resp(conn, :no_content, "")

        {:error, :changeset} ->
          conn
          |> put_status(:bad_request)
          |> json(%{error: "Invalid alter attributes.", code: "invalid_alter_attributes"})

        {:error, :no_alter_id} ->
          conn
          |> put_status(:not_found)
          |> json(%{error: "Alter not found.", code: "alter_not_found"})

        {:error, :alias_taken} ->
          conn
          |> put_status(:bad_request)
          |> json(%{error: "That alias is already taken.", code: "alias_taken"})

        _ ->
          conn
          |> put_status(:internal_server_error)
          |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
      end
    end
  end

  def upload_avatar(conn, %{"id" => id, "file" => %Plug.Upload{} = file}) do
    system_id = conn.private[:guardian_default_resource]

    alter_id = Alters.resolve_alter({:system, system_id}, {:id, id})

    if alter_id != nil do
      case AlterUtils.upload_avatar({:system, system_id}, {:id, id}, file.path) do
        :ok ->
          send_resp(conn, :no_content, "")

        {:error, _} ->
          conn
          |> put_status(:internal_server_error)
          |> json(%{error: "An error occurred while uploading the file.", code: "unknown_error"})
      end
    else
      conn
      |> put_status(:not_found)
      |> json(%{error: "Alter not found.", code: "alter_not_found"})
    end
  end

  def delete_avatar(conn, %{"id" => id}) do
    system_id = conn.private[:guardian_default_resource]

    case Alters.update_alter({:system, system_id}, {:id, id}, %{avatar_url: nil}) do
      :ok ->
        Octocon.Utils.nuke_existing_avatars!(system_id, id)
        send_resp(conn, :no_content, "")

      {:error, :changeset} ->
        conn
        |> put_status(:bad_request)
        |> json(%{error: "Invalid alter attributes.", code: "invalid_alter_attributes"})

      {:error, :no_alter_id} ->
        conn
        |> put_status(:not_found)
        |> json(%{error: "Alter not found.", code: "alter_not_found"})

      _ ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end
end
