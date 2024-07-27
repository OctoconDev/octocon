import Config

# Configures Swoosh API Client
config :swoosh, api_client: Swoosh.ApiClient.Finch, finch_name: Octocon.Finch

# Disable Swoosh Local Memory Storage
config :swoosh, local: false

config :octocon, :nostrum_scope, :global

config :mnesia, dir: '/mnesia'

# TODO: Do not print debug messages in production
# config :logger, level: :info
config :logger, level: :info

config :sentry,
  environment_name: :prod

# tags: %{
#   env: "production"
# }

# Runtime production configuration, including reading
# of environment variables, is done on config/runtime.exs.
