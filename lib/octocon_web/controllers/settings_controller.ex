defmodule OctoconWeb.SettingsController do
  use OctoconWeb, :controller

  alias Octocon.Accounts
  alias Octocon.NotificationTokens

  alias Octocon.Utils.User, as: UserUtils

  alias Octocon.Workers.PluralKitImportWorker
  alias Octocon.Workers.SimplyPluralImportWorker

  def update_username(conn, %{"username" => username}) do
    system_id = conn.private[:guardian_default_resource]
    user = Accounts.get_user({:system, system_id})

    if username == user.username do
      conn
      |> put_status(:bad_request)
      |> json(%{
        error: "Your username is already set to \"#{username}\".",
        code: "username_already_set"
      })
    else
      case Accounts.update_user(user, %{username: username}) do
        {:ok, _} ->
          conn
          |> put_status(:ok)
          |> json(%{message: "Your username has been changed to \"#{username}\"."})

        {:error,
         %Ecto.Changeset{
           errors: [
             username: {"has already been taken", _}
           ]
         }} ->
          conn
          |> put_status(:bad_request)
          |> json(%{
            error: "The username \"#{username}\" is already taken.",
            code: "username_taken"
          })

        {:error,
         %Ecto.Changeset{
           #  errors: [
           #    username: {"has invalid format", _}
           #  ]
         }} ->
          conn
          |> put_status(:bad_request)
          |> json(%{
            error:
              "The username \"#{username}\" is invalid. It must satisfy the following criteria:\n\n- Between 5-16 characters\n- Only contains letters, numbers, dashes, and underscores\n- Does not start or end with a symbol\n- Does not consist of seven lowercase letters in a row (like a system ID)",
            code: "username_invalid"
          })
      end
    end
  end

  def clear_username(conn, _params) do
    system_id = conn.private[:guardian_default_resource]
    user = Accounts.get_user({:system, system_id})

    case Accounts.update_user(user, %{username: nil}) do
      {:ok, _} ->
        conn
        |> put_status(:ok)
        |> json(%{message: "Your username has been cleared."})

      {:error, _} ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end

  def create_custom_field(conn, %{"name" => name, "type" => type}) do
    system_id = conn.private[:guardian_default_resource]

    atom_type =
      try do
        String.to_existing_atom(type)
      rescue
        _ -> :text
      end

    case Accounts.add_field({:system, system_id}, %{name: name, type: atom_type}) do
      {:ok, _} ->
        conn
        |> send_resp(:created, "")

      {:error, _} ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end

  def remove_custom_field(conn, %{"id" => id}) do
    system_id = conn.private[:guardian_default_resource]

    case Accounts.remove_field({:system, system_id}, id) do
      {:ok, _} ->
        conn
        |> send_resp(:no_content, "")

      {:error, _} ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end

  def edit_custom_field(conn, %{"id" => id} = params) do
    system_id = conn.private[:guardian_default_resource]

    attrs =
      %{}
      |> then(fn attrs ->
        if Map.has_key?(params, "name") do
          Map.put(attrs, :name, params["name"])
        else
          attrs
        end
      end)
      |> then(fn attrs ->
        if Map.has_key?(params, "security_level") do
          try do
            Map.put(attrs, :security_level, String.to_existing_atom(params["security_level"]))
          rescue
            _ -> attrs
          end
        else
          attrs
        end
      end)
      |> then(fn attrs ->
        if Map.has_key?(params, "locked") do
          Map.put(attrs, :locked, params["locked"])
        else
          attrs
        end
      end)

    case Accounts.edit_field({:system, system_id}, id, attrs) do
      {:ok, _} ->
        conn
        |> send_resp(:no_content, "")

      {:error, _} ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end

  def relocate_custom_field(conn, %{"id" => id, "index" => index}) when is_binary(index) do
    case Integer.parse(index) do
      :error ->
        conn
        |> put_status(:bad_request)
        |> json(%{error: "The index must be an integer.", code: "invalid_index"})

      {idx, _} when idx < 0 ->
        conn
        |> put_status(:bad_request)
        |> json(%{
          error: "The index must be greater than or equal to zero.",
          code: "invalid_index"
        })

      {idx, _} ->
        relocate_custom_field(conn, %{"id" => id, "index" => idx})
    end
  end

  def relocate_custom_field(conn, %{"id" => id, "index" => index}) when is_number(index) do
    system_id = conn.private[:guardian_default_resource]

    case Accounts.relocate_field({:system, system_id}, id, index) do
      {:ok, _} ->
        conn
        |> send_resp(:no_content, "")

      {:error, _} ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end

  def upload_avatar(conn, %{"file" => %Plug.Upload{} = file}) do
    system_id = conn.private[:guardian_default_resource]
    system = Accounts.get_user!({:system, system_id})

    case UserUtils.upload_avatar(system, file.path) do
      {:ok, _} ->
        send_resp(conn, :no_content, "")

      {:error, _} ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An error occurred while uploading the file.", code: "unknown_error"})
    end
  end

  def delete_avatar(conn, _params) do
    system_id = conn.private[:guardian_default_resource]
    system = Accounts.get_user!({:system, system_id})

    case Accounts.update_user(system, %{avatar_url: nil}) do
      {:ok, _} ->
        Octocon.Utils.nuke_existing_avatars!(system_id, "self")
        send_resp(conn, :no_content, "")

      _ ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end

  def import_pk(conn, %{"token" => token}) do
    system_id = conn.private[:guardian_default_resource]

    %{"system_id" => system_id, "pk_token" => String.trim(token)}
    |> PluralKitImportWorker.new()
    |> Octocon.ObanHandler.insert()

    send_resp(conn, :no_content, "")
  end

  def import_sp(conn, %{"token" => token}) do
    system_id = conn.private[:guardian_default_resource]

    %{"system_id" => system_id, "sp_token" => String.trim(token)}
    |> SimplyPluralImportWorker.new()
    |> Octocon.ObanHandler.insert()

    send_resp(conn, :no_content, "")
  end

  def add_push_token(conn, %{"token" => token}) do
    system_id = conn.private[:guardian_default_resource]

    case NotificationTokens.add_notification_token({:system, system_id}, token) do
      {:ok, _} ->
        send_resp(conn, :no_content, "")

      {:error, _} ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "Failed to synchronize your push notification token."})
    end
  end

  def invalidate_push_token(conn, %{"token" => token}) do
    system_id = conn.private[:guardian_default_resource]

    case NotificationTokens.invalidate_notification_token({:system, system_id}, token) do
      {1, _} ->
        send_resp(conn, :no_content, "")

      _ ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "Failed to desynchronize your push notification token."})
    end
  end

  def unlink_discord(conn, _params) do
    system_id = conn.private[:guardian_default_resource]

    case Accounts.unlink_discord_from_user({:system, system_id}) do
      {:ok, _} ->
        send_resp(conn, :no_content, "")

      {:error, :not_linked} ->
        conn
        |> put_status(:bad_request)
        |> json(%{error: "You are not linked to a Discord account.", code: "not_linked"})

      {:error, :cannot_unlink} ->
        conn
        |> put_status(:bad_request)
        |> json(%{
          error:
            "You cannot unlink your Discord account unless you are also logged in with another authentication method (like Google).",
          code: "cannot_unlink"
        })

      {:error, _} ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end

  def unlink_email(conn, _params) do
    system_id = conn.private[:guardian_default_resource]

    case Accounts.unlink_email_from_user({:system, system_id}) do
      {:ok, _} ->
        send_resp(conn, :no_content, "")

      {:error, :not_linked} ->
        conn
        |> put_status(:bad_request)
        |> json(%{error: "You are not linked to an email account.", code: "not_linked"})

      {:error, :cannot_unlink} ->
        conn
        |> put_status(:bad_request)
        |> json(%{
          error:
            "You cannot unlink your email account unless you are also logged in with another authentication method (like Discord).",
          code: "cannot_unlink"
        })

      {:error, _} ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end

  def update_description(conn, %{"description" => description}) do
    system_id = conn.private[:guardian_default_resource]
    system = Accounts.get_user!({:system, system_id})

    case Accounts.update_user(system, %{description: description}) do
      {:ok, _} ->
        send_resp(conn, :no_content, "")

      {:error, %Ecto.Changeset{}} ->
        conn
        |> put_status(:bad_request)
        |> json(%{
          error: "The description must be at most 3,000 characters.",
          code: "description_invalid"
        })

      {:error, _} ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end
end
