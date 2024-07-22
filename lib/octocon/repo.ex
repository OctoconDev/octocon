# defmodule Octocon.Repo do
#   use Ecto.Repo,
#     otp_app: :octocon,
#     adapter: Ecto.Adapters.Postgres
# end
defmodule Octocon.Repo.Local do
  use Ecto.Repo,
    otp_app: :octocon,
    adapter: Ecto.Adapters.Postgres

  @env Mix.env()

  def init(_type, config) do
    Fly.Postgres.config_repo_url(config, @env)
  end
end

defmodule Octocon.Repo do
  use Fly.Repo, local_repo: Octocon.Repo.Local
end
