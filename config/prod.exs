import Config

config :octocon, :nostrum_scope, :global

config :mnesia, dir: ~c"/mnesia"

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
