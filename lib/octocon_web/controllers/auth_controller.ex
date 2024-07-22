defmodule OctoconWeb.AuthController do
  use OctoconWeb, :controller

  alias Octocon.Accounts
  alias Octocon.Accounts.User
  alias Octocon.Auth.Guardian
  alias Octocon.Repo

  plug Ueberauth

  def request(conn, _params) do
    conn
    |> send_resp(403, "")
  end

  def callback(%{assigns: %{ueberauth_auth: %{uid: discord_id}}} = conn, %{
        "provider" => "discord"
      }) do
    user =
      case Repo.get_by(User, discord_id: discord_id) do
        nil -> Accounts.create_user_from_discord(discord_id) |> elem(1)
        user -> user
      end

    {:ok, token, _claims} = Guardian.encode_and_sign(user.id)
    redirect(conn, external: "https://octocon.app/deep/auth/token?token=#{token}&id=#{user.id}")
  end

  def callback(%{assigns: %{ueberauth_auth: %{info: %{email: email}}}} = conn, %{
        "provider" => "google"
      }) do
    user =
      case Repo.get_by(User, email: email) do
        nil -> Accounts.create_user_from_email(email) |> elem(1)
        user -> user
      end

    {:ok, token, _claims} = Guardian.encode_and_sign(user.id)
    redirect(conn, external: "https://octocon.app/deep/auth/token?token=#{token}&id=#{user.id}")
  end

  def callback(conn, _params) do
    conn
    |> put_status(403)
    |> text("Failed to authenticate. Did you reload the page or copy-paste the URL?")
  end
end
