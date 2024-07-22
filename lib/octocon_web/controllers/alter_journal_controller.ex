defmodule OctoconWeb.AlterJournalController do
  use OctoconWeb, :controller

  alias Octocon.Journals

  def index(conn, %{"id" => alter_id}) do
    system_id = conn.private[:guardian_default_resource]

    case Journals.get_alter_journal_entries({:system, system_id}, {:id, alter_id}, fields: :bare) do
      {:error, :no_alter} ->
        conn
        |> put_status(:not_found)
        |> json(%{error: "Alter not found.", code: "alter_not_found"})

      {:error, :changeset} ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred", code: "unknown_error"})

      entries ->
        conn
        |> render(:index, entries: entries)
    end
  end

  def show(conn, %{"journal_id" => journal_id}) do
    system_id = conn.private[:guardian_default_resource]

    case Journals.get_alter_journal_entry({:system, system_id}, journal_id) do
      nil ->
        conn
        |> put_status(:not_found)
        |> json(%{error: "Journal entry not found.", code: "journal_entry_not_found"})

      entry ->
        conn
        |> render(:show, entry: entry)
    end
  end

  def create(conn, %{"id" => alter_id, "title" => title}) do
    system_id = conn.private[:guardian_default_resource]

    case Journals.create_alter_journal_entry({:system, system_id}, {:id, alter_id}, title) do
      {:error, :no_alter} ->
        conn
        |> put_status(:not_found)
        |> json(%{error: "Alter not found.", code: "alter_not_found"})

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

  def delete(conn, %{"journal_id" => journal_id}) do
    system_id = conn.private[:guardian_default_resource]

    case Journals.delete_alter_journal_entry({:system, system_id}, journal_id) do
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

  def update(conn, %{"journal_id" => journal_id} = attrs) do
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
      case Journals.update_alter_journal_entry({:system, system_id}, journal_id, attrs) do
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
end