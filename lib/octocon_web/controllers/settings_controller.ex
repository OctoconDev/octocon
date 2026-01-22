defmodule OctoconWeb.SettingsController do
  use OctoconWeb, :controller

  alias Octocon.{
    Accounts,
    NotificationTokens
  }

  alias Octocon.Utils.User, as: UserUtils

  alias Octocon.Workers.{
    PluralKitImportWorker,
    SimplyPluralImportWorker
  }

  def reset_encryption(conn, %{}) do
    system_id = conn.private[:guardian_default_resource]

    case Accounts.wipe_encrypted_data({:system, system_id}) do
      :ok ->
        conn
        |> send_resp(:no_content, "")

      {:error, _} ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end

  def recover_encryption(conn, %{"recovery_code" => encrypted_recovery_code}) do
    system_id = conn.private[:guardian_default_resource]
    user = Accounts.get_user!({:system, system_id})

    case Hammer.check_rate("encryption_key:#{system_id}", :timer.seconds(5), 1) do
      {:allow, _count} ->
        Octocon.ClusterUtils.run_on_sidecar(
          fn ->
            case decrypt_recovery_code(encrypted_recovery_code) do
              {:ok, recovery_code} ->
                old_checksum = user.encryption_key_checksum

                key = generate_encryption_key(user, recovery_code)

                new_checksum =
                  :crypto.hash(:sha256, key)
                  |> Base.encode64()
                  |> String.slice(0..8)

                if old_checksum == new_checksum do
                  conn
                  |> put_status(:ok)
                  |> json(%{data: %{key: Base.encode64(key)}})
                else
                  conn
                  |> put_status(:bad_request)
                  |> json(%{
                    error: "Invalid recovery code.",
                    code: "invalid_recovery_code"
                  })
                end

              {:error, _} ->
                conn
                |> put_status(:bad_request)
                |> json(%{
                  error: "Failed to decrypt recovery code.",
                  code: "decryption_error"
                })
            end
          end,
          timeout: 10_000
        )

      _ ->
        conn
        |> put_status(:too_many_requests)
        |> json(%{
          error:
            "You have requested too many encryption key recoveries. Please wait a few seconds before trying again.",
          code: "rate_limited"
        })
    end
  end

  def setup_encryption(conn, %{
        "recovery_code" => encrypted_recovery_code
        # "public_key" => public_key_pem
      }) do
    system_id = conn.private[:guardian_default_resource]
    user = Accounts.get_user!({:system, system_id})

    case Hammer.check_rate("encryption_key:#{system_id}", :timer.seconds(5), 1) do
      {:allow, _count} ->
        case decrypt_recovery_code(encrypted_recovery_code) do
          {:ok, recovery_code} ->
            # case encrypt_key_with_public_key(user, recovery_code, public_key_pem) do
            #   {:ok, encrypted_key} ->
            #     if is_init do
            #       Accounts.update_user(user, %{encryption_initialized: true})
            #     end

            #     conn
            #     |> put_status(:ok)
            #     |> json(%{data: %{key: encrypted_key}})

            #   {:error, e} ->
            #     conn
            #     |> put_status(:internal_server_error)
            #     |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
            # end

            key = generate_encryption_key(user, recovery_code)

            # Only take a few bytes for the checksum
            key_checksum =
              :crypto.hash(:sha256, key)
              |> Base.encode64()
              |> String.slice(0..8)

            Accounts.update_user(user, %{
              encryption_initialized: true,
              encryption_key_checksum: key_checksum
            })

            conn
            |> put_status(:ok)
            |> json(%{data: %{key: Base.encode64(key)}})

          {:error, _} ->
            conn
            |> put_status(:internal_server_error)
            |> json(%{error: "Failed to decrypt recovery code.", code: "decryption_error"})
        end

      _ ->
        conn
        |> put_status(:too_many_requests)
        |> json(%{
          error: "You have requested too many encryption keys. Please wait before trying again.",
          code: "rate_limited"
        })
    end
  end

  # defp encrypt_key_with_public_key(
  #   user,
  #   recovery_code, public_key_pem) do
  #   public_key =
  #     :public_key.pem_decode(public_key_pem)
  #     |> hd()
  #     |> :public_key.pem_entry_decode()

  #   key = generate_encryption_key(user, recovery_code)

  #   encrypted_key =
  #     :public_key.encrypt_public(key, public_key, rsa_padding: :rsa_pkcs1_oaep_padding, rsa_mgf1_md: :sha256)
  #     |> Base.encode64()

  #   {:ok, encrypted_key}
  # rescue
  #   e -> {:error, e}
  # end

  defp generate_encryption_key(%Octocon.Accounts.User{id: user_id, salt: salt}, recovery_code) do
    pepper = Application.get_env(:octocon, :pepper)
    hash_data = pepper <> user_id <> recovery_code
    hash = :crypto.hash(:sha256, hash_data)

    hash
    |> Argon2.Base.hash_password(salt, t_cost: 12, hash_len: 32, format: :raw_hash)
    |> Base.decode16!(case: :lower)
    |> Base.encode64()
  end

  defp decrypt_recovery_code(encrypted_recovery_code) do
    private_key_pem = Application.get_env(:octocon, :private_key_pem)

    private_jwk = JOSE.JWK.from_pem(private_key_pem)

    {recovery_code, _jwe} = JOSE.JWE.block_decrypt(private_jwk, encrypted_recovery_code)

    {:ok, recovery_code}
  rescue
    e -> {:error, e}
  end

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

    PluralKitImportWorker.perform(%{"system_id" => system_id, "pk_token" => String.trim(token)})

    send_resp(conn, :no_content, "")
  end

  def import_sp(conn, %{"token" => token}) do
    system_id = conn.private[:guardian_default_resource]

    SimplyPluralImportWorker.perform(%{
      "system_id" => system_id,
      "sp_token" => String.trim(token)
    })

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
    case NotificationTokens.invalidate_notification_token(token) do
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
            "You cannot unlink your Discord account unless you are also logged in with another authentication method (Google or Apple).",
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
            "You cannot unlink your email account unless you are also logged in with another authentication method (Discord or Apple).",
          code: "cannot_unlink"
        })

      {:error, _} ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end

  def unlink_apple(conn, _params) do
    system_id = conn.private[:guardian_default_resource]

    case Accounts.unlink_apple_from_user({:system, system_id}) do
      {:ok, _} ->
        send_resp(conn, :no_content, "")

      {:error, :not_linked} ->
        conn
        |> put_status(:bad_request)
        |> json(%{error: "You are not linked to an Apple account.", code: "not_linked"})

      {:error, :cannot_unlink} ->
        conn
        |> put_status(:bad_request)
        |> json(%{
          error:
            "You cannot unlink your Apple account unless you are also logged in with another authentication method (Discord or Google).",
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

  def wipe_alters(conn, _params) do
    system_id = conn.private[:guardian_default_resource]

    case Accounts.wipe_alters({:system, system_id}) do
      :ok ->
        send_resp(conn, :no_content, "")

      {:error, _} ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end

  def delete_account(conn, _params) do
    system_id = conn.private[:guardian_default_resource]

    case Accounts.delete_user({:system, system_id}) do
      :ok ->
        send_resp(conn, :no_content, "")

      {:error, _} ->
        conn
        |> put_status(:internal_server_error)
        |> json(%{error: "An unknown error occurred.", code: "unknown_error"})
    end
  end
end
