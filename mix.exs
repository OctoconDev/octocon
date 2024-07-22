defmodule Octocon.MixProject do
  use Mix.Project

  def project do
    [
      app: :octocon,
      version: "0.1.0",
      elixir: "~> 1.14",
      elixirc_paths: elixirc_paths(Mix.env()),
      start_permanent: Mix.env() == :prod,
      aliases: aliases(),
      deps: deps()
    ]
  end

  # Configuration for the OTP application.
  #
  # Type `mix help compile.app` for more information.
  def application do
    [
      mod: {Octocon.Application, []},
      included_applications: [:nostrum],
      extra_applications: [
        :logger,
        :runtime_tools,
        :os_mon,
        :timex,
        :certifi,
        :gun,
        :inets,
        :jason,
        :mime
      ]
    ]
  end

  # Specifies which paths to compile per environment.
  defp elixirc_paths(:test), do: ["lib", "test/support"]
  defp elixirc_paths(_), do: ["lib"]

  # Specifies your project dependencies.
  #
  # Type `mix help deps` for examples and options.
  defp deps do
    [
      # Phoenix boilerplate
      {:phoenix, "~> 1.7.14"},
      {:phoenix_ecto, "~> 4.6.1"},
      {:ecto_sql, "~> 3.11"},
      {:ecto_psql_extras, "~> 0.8.0"},
      {:postgrex, "~> 0.18.0"},
      {:phoenix_live_dashboard, "~> 0.8.4"},
      {:swoosh, "~> 1.16"},
      {:finch, "~> 0.18"},
      {:telemetry_metrics, "~> 1.0"},
      {:telemetry_poller, "~> 1.1"},
      {:gettext, "~> 0.24"},
      {:jason, "~> 1.4"},
      # Use Bandit instead of Cowboy
      {:bandit, "~> 1.5.5"},
      {:websock_adapter, "~> 0.5.6"},
      # Authentication
      {:guardian, "~> 2.0"},
      {:guardian_db, "~> 2.0"},
      {:ueberauth, "~> 0.10"},
      {:ueberauth_google, "~> 0.10"},
      {:ueberauth_discord, "~> 0.6"},
      {:nanoid, "~> 2.1.0"},
      # CI
      {:credo, "~> 1.7", only: [:dev, :test], runtime: false},
      # Push notifications
      {:pigeon, "~> 2.0.0-rc.1"},
      # Discord
      {:certifi, "~> 2.13", override: true},
      # {:nostrum, "0.9.1", runtime: false},
      {:nostrum, github: "Kraigie/nostrum", branch: "master", override: true, runtime: false},
      # {:nosedrum, "~> 0.6"},
      # {:nosedrum,
      # github: "jchristgit/nosedrum", branch: "master", override: true},
      # Caching
      {:cachex, "~> 3.6"},
      # Background jobs
      {:oban, "~> 2.17.11"},
      # Object storage
      {:waffle, "~> 1.1"},
      {:image, "~> 0.48.1"},
      {:ex_aws, "~> 2.5"},
      {:ex_aws_s3, "~> 2.5"},
      {:sweet_xml, "~> 0.7.4"},
      {:hackney, "~> 1.20"},
      # Analytics
      {:sentry, "~> 10.6.1"},
      # Rate limiting
      {:hammer, "~> 6.2"},
      {:hammer_plug, "~> 3.0"},
      # Utils
      {:timex, "~> 3.7"},
      # Distribution
      {:fly_postgres, "~> 0.3.3"},
      {:dns_cluster, "~> 0.1.3"},
      {:horde, "~> 0.9.0"},
      {:highlander, "~> 0.2.1"},
      # Time-series data
      {:timescale, "~> 0.1.1"}
    ]
  end

  # Aliases are shortcuts or tasks specific to the current project.
  # For example, to install project dependencies and perform other setup tasks, run:
  #
  #     $ mix setup
  #
  # See the documentation for `Mix` for more info on aliases.
  defp aliases do
    [
      setup: ["deps.get", "ecto.setup"],
      "ecto.setup": [
        "ecto.create",
        "ecto.migrate",
        "run priv/repo/seeds.exs",
        "run priv/msg_repo/seeds.exs"
      ],
      "ecto.reset": ["ecto.drop", "ecto.setup"],
      "assets.deploy": [
        "phx.digest"
      ]
    ]
  end
end
