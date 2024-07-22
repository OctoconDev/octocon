defmodule OctoconWeb.AuthLinkController do
  use OctoconWeb, :controller

  require Logger

  alias Octocon.Global.LinkTokenRegistry

  plug :put_link_token
  plug Ueberauth, providers: [:google_link, :discord_link]

  def request(conn, _params) do
    conn
    |> send_resp(403, "")
  end

  def put_link_token(%{params: %{"link_token" => link_token}} = conn, _params) do
    Logger.info("Link token injected into session: #{link_token}")

    conn
    |> put_session(:link_token, link_token)
  end

  def put_link_token(conn, _params) do
    conn
  end

  def callback(%{assigns: %{ueberauth_auth: auth_data}} = conn, %{"provider" => provider}) do
    link_token = get_session(conn, :link_token)
    Logger.info("Link token received: #{link_token}")

    case Fly.RPC.rpc_primary(fn -> LinkTokenRegistry.get(link_token) end) do
      nil ->
        conn
        |> delete_session(:link_token)
        |> put_status(403)
        |> text("This link token is invalid or has expired.")

      system_id when is_binary(system_id) ->
        Logger.info("Recovered ID: #{system_id}")
        LinkTokenRegistry.delete(link_token)

        try_link_account_wrapper(provider, conn, system_id, auth_data)
    end
  end

  def callback(conn, _params) do
    conn
    |> put_status(403)
    |> text("Failed to authenticate. Did you reload the page or copy-paste the URL?")
  end

  def try_link_account_wrapper(provider, conn, system_id, auth_data) do
    case Octocon.Accounts.get_user({:system, system_id}) do
      nil ->
        conn
        |> put_status(403)
        |> json(%{error: "System not found"})

      user ->
        try_link_account(provider, conn, user, auth_data)
    end
  end

  def try_link_account("discord", conn, user, %{uid: discord_id}) do
    case Octocon.Accounts.link_discord_to_user(user, to_string(discord_id)) do
      {:error, :already_linked} ->
        conn
        |> put_status(403)
        |> json(%{
          error: "A Discord account is already linked to this account; please unlink it first."
        })

      {:error,
       %Ecto.Changeset{
         errors: [
           discord_id: {"has already been taken", _}
         ]
       }} ->
        conn
        |> put_status(500)
        |> json(%{error: "This Discord account is already linked to another account."})

      {:ok, _} ->
        conn
        |> redirect(external: "https://octocon.app/deep/link_success/discord")
    end
  end

  def try_link_account("google", conn, user, %{info: %{email: email}}) do
    case Octocon.Accounts.link_email_to_user(user, email) do
      {:error, :already_linked} ->
        conn
        |> put_status(403)
        |> json(%{
          error: "A Google account is already linked to this account; please unlink it first."
        })

      {:error,
       %Ecto.Changeset{
         errors: [
           email: {"has already been taken", _}
         ]
       }} ->
        conn
        |> put_status(500)
        |> json(%{error: "This email address is already linked to another account."})

      {:ok, _} ->
        conn
        |> redirect(external: "https://octocon.app/deep/link_success/google")
    end
  end
end
