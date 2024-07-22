defmodule OctoconWeb.AuthLinkTokenController do
  use OctoconWeb, :controller

  require Logger

  alias Octocon.Global.LinkTokenRegistry

  def get(conn, _params) do
    system_id = conn.private[:guardian_default_resource]
    link_token = Fly.RPC.rpc_primary(fn -> LinkTokenRegistry.put(system_id) end)

    Logger.info("Link token generated for #{system_id}: #{link_token}")

    conn
    |> put_status(200)
    |> json(%{data: %{token: link_token}})
  end
end
