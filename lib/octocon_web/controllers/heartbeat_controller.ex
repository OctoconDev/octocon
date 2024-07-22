defmodule OctoconWeb.HeartbeatController do
  use OctoconWeb, :controller

  def index(conn, _params) do
    conn
    |> put_status(:ok)
    |> json(%{response: "ACK"})
  end
end
