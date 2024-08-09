defmodule OctoconWeb.GlobalJournalController do
  use OctoconWeb, :controller

  alias Octocon.Journals

  def index(conn, _params) do
    system_id = conn.private[:guardian_default_resource]

    Journals.get_global_journal_entries({:system, system_id}, fields: :bare)
    |> then(fn entries -> render(conn, :index, entries: entries) end)
  end

  def show(conn, %{"id" => journal_id}) do
    system_id = conn.private[:guardian_default_resource]

    case Journals.get_global_journal_entry({:system, system_id}, journal_id) do
      nil ->
        conn
        |> put_status(:not_found)
        |> json(%{error: "Journal entry not found.", code: "journal_entry_not_found"})

      entry ->
        conn
        |> render(:show, entry: entry)
    end
  end

  def create(conn, %{"title" => title}) do
    system_id = conn.private[:guardian_default_resource]

    case Journals.create_global_journal_entry({:system, system_id}, title) do
      {:error, :not_found} ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})

      {:error, :changeset} ->
        conn
        |> put_status(:bad_request)
        |> json(%{
          error: "Invalid journal entry attributes.",
          code: "invalid_journal_entry_attributes"
        })

      {:ok, entry} ->
        conn
        |> put_status(:created)
        |> render(:show, entry: entry)
    end
  end

  def delete(conn, %{"id" => journal_id}) do
    system_id = conn.private[:guardian_default_resource]

    case Journals.delete_global_journal_entry({:system, system_id}, journal_id) do
      {:error, :not_found} ->
        conn
        |> put_status(:not_found)
        |> json(%{error: "Journal entry not found.", code: "journal_entry_not_found"})

      :ok ->
        conn
        |> put_status(:no_content)
        |> send_resp(:no_content, "")
    end
  end

  def update(conn, %{"id" => journal_id} = attrs) do
    system_id = conn.private[:guardian_default_resource]

    attrs =
      Map.take(attrs, [
        "title",
        "content",
        "color"
      ])
      |> Enum.map(fn {k, v} -> {String.to_existing_atom(k), v} end)
      |> Map.new()

    if map_size(attrs) == 0 do
      conn
      |> put_status(:bad_request)
      |> json(%{
        error: "No valid journal entry attributes provided.",
        code: "no_journal_attributes"
      })
    else
      case Journals.update_global_journal_entry({:system, system_id}, journal_id, attrs) do
        {:ok, _entry} ->
          send_resp(conn, :no_content, "")

        {:error, :not_found} ->
          conn
          |> put_status(:not_found)
          |> json(%{error: "Journal entry not found.", code: "journal_entry_not_found"})

        {:error, :changeset} ->
          conn
          |> put_status(:bad_request)
          |> json(%{
            error: "Invalid journal entry attributes.",
            code: "invalid_journal_attributes"
          })

        _ ->
          conn
          |> put_status(:internal_server_error)
          |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
      end
    end
  end

  def lock(conn, %{"id" => journal_id}) do
    system_id = conn.private[:guardian_default_resource]

    case Journals.update_global_journal_entry({:system, system_id}, journal_id, %{locked: true}) do
      {:ok, _entry} ->
        conn
        |> put_status(:no_content)
        |> send_resp(:no_content, "")

      {:error, :not_found} ->
        conn
        |> put_status(:not_found)
        |> json(%{error: "Journal entry not found.", code: "journal_entry_not_found"})

      _ ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end

  def unlock(conn, %{"id" => journal_id}) do
    system_id = conn.private[:guardian_default_resource]

    case Journals.update_global_journal_entry({:system, system_id}, journal_id, %{locked: false}) do
      {:ok, _entry} ->
        conn
        |> put_status(:no_content)
        |> send_resp(:no_content, "")

      {:error, :not_found} ->
        conn
        |> put_status(:not_found)
        |> json(%{error: "Journal entry not found.", code: "journal_entry_not_found"})

      _ ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end

  def pin(conn, %{"id" => journal_id}) do
    system_id = conn.private[:guardian_default_resource]

    case Journals.update_global_journal_entry({:system, system_id}, journal_id, %{pinned: true}) do
      {:ok, _entry} ->
        conn
        |> put_status(:no_content)
        |> send_resp(:no_content, "")

      {:error, :not_found} ->
        conn
        |> put_status(:not_found)
        |> json(%{error: "Journal entry not found.", code: "journal_entry_not_found"})

      _ ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end

  def unpin(conn, %{"id" => journal_id}) do
    system_id = conn.private[:guardian_default_resource]

    case Journals.update_global_journal_entry({:system, system_id}, journal_id, %{pinned: false}) do
      {:ok, _entry} ->
        conn
        |> put_status(:no_content)
        |> send_resp(:no_content, "")

      {:error, :not_found} ->
        conn
        |> put_status(:not_found)
        |> json(%{error: "Journal entry not found.", code: "journal_entry_not_found"})

      _ ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end

  def attach_alter(conn, %{"id" => journal_id, "alter_id" => alter_id})
      when is_integer(alter_id) do
    system_id = conn.private[:guardian_default_resource]

    case Journals.attach_alter_to_global_entry({:system, system_id}, journal_id, {:id, alter_id}) do
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

  def attach_alter(conn, %{"id" => journal_id, "alter_id" => alter_id})
      when is_binary(alter_id) do
    case parse_integer(alter_id) do
      {:ok, parsed_id} ->
        attach_alter(conn, %{"id" => journal_id, "alter_id" => parsed_id})

      :error ->
        conn
        |> put_status(:bad_request)
        |> json(%{error: "Invalid alter ID.", code: "invalid_alter_id"})
    end
  end

  def detach_alter(conn, %{"id" => journal_id, "alter_id" => alter_id})
      when is_integer(alter_id) do
    system_id = conn.private[:guardian_default_resource]

    case Journals.detach_alter_from_global_entry(
           {:system, system_id},
           journal_id,
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
          error: "Journal entry not found or that alter is not attached.",
          code: "journal_entry_or_alter_not_found"
        })

      _ ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end

  def detach_alter(conn, %{"id" => journal_id, "alter_id" => alter_id})
      when is_binary(alter_id) do
    case parse_integer(alter_id) do
      {:ok, parsed_id} ->
        detach_alter(conn, %{"id" => journal_id, "alter_id" => parsed_id})

      :error ->
        conn
        |> put_status(:bad_request)
        |> json(%{error: "Invalid alter ID.", code: "invalid_alter_id"})
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
