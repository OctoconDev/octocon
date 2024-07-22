defmodule Octocon.MessageRepo do
  use Ecto.Repo,
    otp_app: :octocon,
    adapter: Ecto.Adapters.Postgres
end
