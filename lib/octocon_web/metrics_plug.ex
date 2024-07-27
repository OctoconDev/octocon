defmodule OctoconWeb.MetricsPlug do
  @moduledoc false
  use Plug.Builder

  plug PromEx.Plug, path: "/", prom_ex_module: Octocon.PromEx

  plug :not_found

  defp not_found(conn, _opts) do
    conn
    |> send_resp(404, "Not found")
  end
end
