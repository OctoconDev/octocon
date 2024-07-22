defmodule OctoconWeb.System.TagController do
  use OctoconWeb, :controller

  import OctoconWeb.Utils.Systems

  alias Octocon.Tags

  def index(conn, %{"system_id" => system_id}) do
    caller_id = conn.private[:guardian_default_resource]

    case parse_system(conn, system_id) do
      {:noreply, conn} ->
        conn

      {:self, system} ->
        render(conn, :index_me, tags: Tags.get_tags({:system, system.id}))

      {:other, system} ->
        render(conn, :index,
          tags: Tags.get_tags_guarded({:system, system.id}, {:system, caller_id})
        )
    end
  end

  def show(conn, %{"system_id" => system_id, "id" => tag_id}) do
    caller_id = conn.private[:guardian_default_resource]

    case parse_system(conn, system_id) do
      {:noreply, conn} ->
        conn

      {:self, _system} ->
        case Tags.get_tag({:system, caller_id}, tag_id) do
          nil ->
            conn
            |> put_status(:not_found)
            |> json(%{error: "Tag not found.", code: "tag_not_found"})

          tag ->
            render(conn, :show_me, tag: tag)
        end

      {:other, system} ->
        case Tags.get_tag_guarded({:system, system.id}, tag_id, {:system, caller_id}) do
          nil ->
            conn
            |> put_status(:not_found)
            |> json(%{error: "Tag not found.", code: "tag_not_found"})

          tag ->
            render(conn, :show, tag: tag)
        end
    end
  end

  def create(conn, %{"name" => name, "parent_tag_id" => parent_tag_id}) do
    system_id = conn.private[:guardian_default_resource]

    case Tags.create_tag({:system, system_id}, name, parent_tag_id) do
      {:error, :not_found} ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})

      {:error, :changeset} ->
        conn
        |> put_status(:bad_request)
        |> json(%{
          error: "Invalid tag attributes.",
          code: "invalid_tag_attributes"
        })

      {:ok, tag} ->
        conn
        |> put_status(:created)
        |> render(:show, tag: tag)
    end
  end

  def create(conn, %{"name" => name}) do
    system_id = conn.private[:guardian_default_resource]

    case Tags.create_tag({:system, system_id}, name) do
      {:error, :not_found} ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})

      {:error, :changeset} ->
        conn
        |> put_status(:bad_request)
        |> json(%{
          error: "Invalid tag attributes.",
          code: "invalid_tag_attributes"
        })

      {:ok, tag} ->
        conn
        |> put_status(:created)
        |> render(:show, tag: tag)
    end
  end

  def delete(conn, %{"id" => tag_id}) do
    system_id = conn.private[:guardian_default_resource]

    case Tags.delete_tag({:system, system_id}, tag_id) do
      {:error, :not_found} ->
        conn
        |> put_status(:not_found)
        |> json(%{error: "Tag not found.", code: "tag_not_found"})

      :ok ->
        conn
        |> put_status(:no_content)
        |> send_resp(:no_content, "")
    end
  end

  def update(conn, %{"id" => tag_id} = attrs) do
    system_id = conn.private[:guardian_default_resource]

    attrs =
      Map.take(attrs, [
        "name",
        "color",
        "description",
        "security_level"
      ])
      |> Enum.map(fn {k, v} -> {String.to_existing_atom(k), v} end)
      |> Map.new()

    if map_size(attrs) == 0 do
      conn
      |> put_status(:bad_request)
      |> json(%{
        error: "No valid tag attributes provided.",
        code: "no_tag_attributes"
      })
    else
      case Tags.update_tag({:system, system_id}, tag_id, attrs) do
        {:ok, _tag} ->
          send_resp(conn, :no_content, "")

        {:error, :not_found} ->
          conn
          |> put_status(:not_found)
          |> json(%{error: "Tag not found.", code: "tag_not_found"})

        {:error, :changeset} ->
          conn
          |> put_status(:bad_request)
          |> json(%{
            error: "Invalid tag attributes.",
            code: "invalid_tag_attributes"
          })

        _ ->
          conn
          |> put_status(:internal_server_error)
          |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
      end
    end
  end

  def attach_alter(conn, %{"id" => tag_id, "alter_id" => alter_id})
      when is_integer(alter_id) do
    system_id = conn.private[:guardian_default_resource]

    case Tags.attach_alter_to_tag({:system, system_id}, tag_id, {:id, alter_id}) do
      :ok ->
        conn
        |> put_status(:no_content)
        |> send_resp(:no_content, "")

      {:error, :alter_not_found} ->
        conn
        |> put_status(:not_found)
        |> json(%{error: "Alter not found.", code: "alter_not_found"})

      {:error, :changeset} ->
        conn
        |> put_status(:bad_request)
        |> json(%{error: "Invalid alter ID.", code: "invalid_alter_id"})

      _ ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end

  def attach_alter(conn, %{"id" => tag_id, "alter_id" => alter_id})
      when is_binary(alter_id) do
    case parse_integer(alter_id) do
      {:ok, parsed_id} ->
        attach_alter(conn, %{"id" => tag_id, "alter_id" => parsed_id})

      :error ->
        conn
        |> put_status(:bad_request)
        |> json(%{error: "Invalid alter ID.", code: "invalid_alter_id"})
    end
  end

  def detach_alter(conn, %{"id" => tag_id, "alter_id" => alter_id})
      when is_integer(alter_id) do
    system_id = conn.private[:guardian_default_resource]

    case Tags.detach_alter_from_tag(
           {:system, system_id},
           tag_id,
           {:id, alter_id}
         ) do
      :ok ->
        conn
        |> put_status(:no_content)
        |> send_resp(:no_content, "")

      {:error, :alter_not_found} ->
        conn
        |> put_status(:not_found)
        |> json(%{error: "Alter not found.", code: "alter_not_found"})

      {:error, :not_found} ->
        conn
        |> put_status(:not_found)
        |> json(%{
          error: "Tag not found or that alter is not attached.",
          code: "tag_or_alter_not_found"
        })

      _ ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end

  def detach_alter(conn, %{"id" => tag_id, "alter_id" => alter_id})
      when is_binary(alter_id) do
    case parse_integer(alter_id) do
      {:ok, parsed_id} ->
        detach_alter(conn, %{"id" => tag_id, "alter_id" => parsed_id})

      :error ->
        conn
        |> put_status(:bad_request)
        |> json(%{error: "Invalid alter ID.", code: "invalid_alter_id"})
    end
  end

  def set_parent(conn, %{"id" => tag_id, "parent_tag_id" => parent_tag_id}) do
    system_id = conn.private[:guardian_default_resource]

    case Tags.set_parent_tag({:system, system_id}, tag_id, parent_tag_id) do
      {:error, :not_found} ->
        conn
        |> put_status(:not_found)
        |> json(%{error: "Tag or parent tag not found.", code: "tag_or_parent_not_found"})

      {:error, :changeset} ->
        conn
        |> put_status(:bad_request)
        |> json(%{error: "Invalid parent tag ID.", code: "invalid_parent_tag_id"})

      :ok ->
        conn
        |> put_status(:no_content)
        |> send_resp(:no_content, "")
    end
  end

  def remove_parent(conn, %{"id" => tag_id}) do
    system_id = conn.private[:guardian_default_resource]

    case Tags.remove_parent_tag({:system, system_id}, tag_id) do
      {:error, :not_found} ->
        conn
        |> put_status(:not_found)
        |> json(%{error: "Tag not found.", code: "tag_not_found"})

      :ok ->
        conn
        |> put_status(:no_content)
        |> send_resp(:no_content, "")
    end
  end

  def parse_integer(value) do
    case Integer.parse(value) do
      :error -> :error
      {id, _} when id < 0 or id > 32_767 -> :error
      {id, _} -> {:ok, id}
    end
  end
end
