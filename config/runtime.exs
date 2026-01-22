import Config

tailscale_api_authkey = System.get_env("TAILSCALE_API_AUTHKEY")

config :octocon, :use_tailscale, tailscale_api_authkey != nil and tailscale_api_authkey != ""
config :octocon, tailscale_api_authkey: tailscale_api_authkey

if config_env() == :prod do
  node_group =
    case System.get_env("FLY_PROCESS_GROUP") do
      "primary" ->
        :primary

      "auxiliary" ->
        :auxiliary

      "sidecar" ->
        :sidecar

      _ ->
        node_group =
          System.get_env("NODE_GROUP") ||
            raise """
            environment variable NODE_GROUP is missing (this node is not running on Fly to auto-detect).
            It should be one of: primary, auxiliary, sidecar.
            """

        String.to_atom(node_group)
    end

  current_db_region =
    case System.get_env("FLY_REGION") do
      "fra" ->
        :eur

      "iad" ->
        :nam

      "syd" ->
        :ocn

      "gru" ->
        :sam

      "bom" ->
        :sas

      "sin" ->
        :eas

      nil ->
        region =
          System.get_env("CURRENT_DB_REGION") ||
            raise """
            environment variable CURRENT_DB_REGION is missing (this node is not running on Fly to auto-detect).
            It should be one of: nam, eur, ocn, eas, sam, sas, gdpr.
            """

        String.to_atom(region)
    end

  current_db_datacenter =
    case current_db_region do
      :nam -> "dedi-us-east"
      :eur -> "fly-fra"
      :gdpr -> "fly-fra"
      :ocn -> "fly-syd"
      :eas -> "fly-sin"
      :sam -> "fly-gru"
      :sas -> "fly-bom"
    end

  config :octocon, :node_group, node_group
  config :octocon, :current_db_region, current_db_region

  # TODO: Only :auxiliary
  # TODO: Rename :auxiliary to :ingress
  if node_group in [:primary, :auxiliary] || System.get_env("PHX_SERVER") do
    config :octocon, OctoconWeb.Endpoint, server: true
  else
    config :octocon, OctoconWeb.Endpoint, server: false
  end

  pool_size =
    if node_group == :sidecar do
      2
    else
      String.to_integer(System.get_env("POOL_SIZE") || "10")
    end

  config :octocon,
         :primary_node_count,
         String.to_integer(System.get_env("PRIMARY_NODE_COUNT") || "1")

  database_password =
    System.get_env("DATABASE_PASSWORD") ||
      raise """
      environment variable DATABASE_PASSWORD is missing.
      """

  database_contact_points =
    System.get_env("DATABASE_CONTACT_POINTS") ||
      raise """
      environment variable DATABASE_CONTACT_POINTS is missing.
      For example: 100.100.127.0,100.100.127.1,100.100.127.2

      Whole env: #{inspect(System.get_env())}
      """

  msg_database_url =
    System.get_env("MSG_DATABASE_URL") ||
      raise """
      environment variable MSG_DATABASE_URL is missing.
      For example: ecto://USER:PASS@HOST/DATABASE
      """

  config :octocon, :proxy_db, System.get_env("OCTO_PROXY_DB") in ~w(true 1)

  config :octocon, Octocon.Repo,
    nodes: String.split(database_contact_points, ","),
    load_balancing:
      {Xandra.Cluster.LoadBalancingPolicy.DCAwareRoundRobin,
       [local_data_center: current_db_datacenter]},
    refresh_topology_interval: :timer.minutes(1),
    sync_connect: :infinity,
    authentication:
      {Xandra.Authenticator.Password, [username: "octo", password: database_password]},
    pool_size: pool_size

  config :octocon, Octocon.MessageRepo,
    url: msg_database_url,
    # , socket_options: maybe_ipv6
    pool_size: String.to_integer(System.get_env("MSG_POOL_SIZE") || "10")

  # The secret key base is used to sign/encrypt cookies and other secrets.
  # A default value is used in config/dev.exs and config/test.exs but you
  # want to use a different value for prod and you most likely don't want
  # to check this value into version control, so we use an environment
  # variable instead.
  secret_key_base =
    System.get_env("SECRET_KEY_BASE") ||
      raise """
      environment variable SECRET_KEY_BASE is missing.
      You can generate one by calling: mix phx.gen.secret
      """

  host =
    System.get_env("PHX_HOST") ||
      raise """
      environment variable PHX_HOST is missing.
      """

  port =
    String.to_integer(
      System.get_env("PORT") ||
        raise("""
        environment variable PORT is missing.
        """)
    )

  config :octocon, OctoconWeb.Endpoint,
    url: [host: host, port: 443, scheme: "https"],
    http: [
      # Enable IPv6 and bind on all interfaces.
      # Set it to  {0, 0, 0, 0, 0, 0, 0, 1} for local network only access.
      # See the documentation on https://hexdocs.pm/plug_cowboy/Plug.Cowboy.html
      # for details about using IPv6 vs IPv4 and loopback vs public addresses.
      ip: {0, 0, 0, 0, 0, 0, 0, 0},
      port: port
    ],
    check_origin: [
      "//*.octocon.app",
      "https://" <> host,
      "wss://" <> host,
      "//localhost",
      "//localhost:8080",
      "http://localhost",
      "http://localhost:8080",
      "//octocon-beta.netlify.app"
    ],
    secret_key_base: secret_key_base,
    live_view: [
      signing_salt:
        System.get_env("LIVE_VIEW_SIGNING_SALT") ||
          raise("""
          environment variable LIVE_VIEW_SIGNING_SALT is missing.
          """)
    ]

  config :ueberauth, Ueberauth.Strategy.Discord.OAuth,
    client_id:
      System.get_env("DISCORD_CLIENT_ID") ||
        raise("""
        environment variable DISCORD_CLIENT_ID is missing.
        """),
    client_secret:
      System.get_env("DISCORD_CLIENT_SECRET") ||
        raise("""
        environment variable DISCORD_CLIENT_SECRET is missing.
        """)

  config :ueberauth, Ueberauth.Strategy.Google.OAuth,
    client_id:
      System.get_env("GOOGLE_CLIENT_ID") ||
        raise("""
        environment variable GOOGLE_CLIENT_ID is missing.
        """),
    client_secret:
      System.get_env("GOOGLE_CLIENT_SECRET") ||
        raise("""
        environment variable GOOGLE_CLIENT_SECRET is missing.
        """)

  config :ueberauth, Ueberauth.Strategy.Apple.OAuth,
    client_id:
      System.get_env("APPLE_CLIENT_ID") ||
        raise("""
        environment variable APPLE_CLIENT_ID is missing.
        """),
    client_secret: {Octocon.Apple, :get_client_secret}

  config :nostrum,
    token:
      System.get_env("DISCORD_TOKEN") ||
        raise("""
        environment variable DISCORD_TOKEN is missing.
        """)

  # Guardian
  config :octocon, Octocon.Auth.Guardian,
    issuer: "octocon",
    secret_key:
      System.get_env("GUARDIAN_SECRET_KEY") ||
        raise("""
        environment variable GUARDIAN_SECRET_KEY is missing.
        """)

  config :octocon,
    pepper:
      System.get_env("ENCRYPTION_PEPPER") ||
        raise("""
        environment variable ENCRYPTION_PEPPER is missing.
        """)

  config :octocon,
    private_key_pem:
      (System.get_env("ENCRYPTION_PRIVATE_KEY") ||
         raise("environment variable ENCRYPTION_PRIVATE_KEY is missing."))
      |> Base.decode64!()

  config :octocon,
    apple_client_id:
      System.get_env("APPLE_CLIENT_ID") ||
        raise("""
        environment variable APPLE_CLIENT_ID is missing.
        """),
    apple_private_key_id:
      System.get_env("APPLE_PRIVATE_KEY_ID") ||
        raise("""
        environment variable APPLE_PRIVATE_KEY_ID is missing.
        """),
    apple_team_id:
      System.get_env("APPLE_TEAM_ID") ||
        raise("""
        environment variable APPLE_TEAM_ID is missing.
        """),
    apple_private_key:
      (System.get_env("APPLE_PRIVATE_KEY") ||
         raise("environment variable APPLE_PRIVATE_KEY is missing."))
      |> String.replace("\\n", "\n")

  config :waffle,
    storage: Waffle.Storage.S3,
    bucket: "octocon",
    asset_host:
      System.get_env("S3_ASSET_HOST") ||
        raise("""
        environment variable S3_ASSET_HOST is missing.
        """)

  config :ex_aws,
    access_key_id:
      System.get_env("S3_ACCESS_KEY_ID") ||
        raise("""
        environment variable S3_ACCESS_KEY_ID is missing.
        """),
    secret_access_key:
      System.get_env("S3_SECRET_ACCESS_KEY") ||
        raise("""
        environment variable S3_SECRET_ACCESS_KEY is missing.
        """)

  config :ex_aws, :s3,
    scheme: "https://",
    host:
      System.get_env("S3_HOST") ||
        raise("""
        environment variable S3_HOST is missing.
        """),
    region:
      System.get_env("S3_REGION") ||
        raise("""
        environment variable S3_REGION is missing.
        """)

  config :octocon, Octocon.FCM,
    adapter: Pigeon.FCM,
    project_id: "octocon-fb",
    service_account_json:
      (System.get_env("FCM_SERVICE_ACCOUNT") ||
         raise("""
         environment variable FCM_SERVICE_ACCOUNT is missing.
         """))
      |> Base.decode64!()

  config :octocon, Octocon.PromEx,
    grafana: [
      host:
        System.get_env("GRAFANA_HOST") || raise("environment variable GRAFANA_HOST is missing."),
      auth_token:
        System.get_env("GRAFANA_TOKEN") || raise("environment variable GRAFANA_TOKEN is missing."),
      upload_dashboards_on_start: false,
      folder_name: "OTP",
      annotate_app_lifecycle: true
    ]

  # redirect_uri: System.get_env("GOOGLE_REDIRECT_URI")

  # ## SSL Support
  #
  # To get SSL working, you will need to add the `https` key
  # to your endpoint configuration:
  #
  #     config :octocon, OctoconWeb.Endpoint,
  #       https: [
  #         ...,
  #         port: 443,
  #         cipher_suite: :strong,
  #         keyfile: System.get_env("SOME_APP_SSL_KEY_PATH"),
  #         certfile: System.get_env("SOME_APP_SSL_CERT_PATH")
  #       ]
  #
  # The `cipher_suite` is set to `:strong` to support only the
  # latest and more secure SSL ciphers. This means old browsers
  # and clients may not be supported. You can set it to
  # `:compatible` for wider support.
  #
  # `:keyfile` and `:certfile` expect an absolute path to the key
  # and cert in disk or a relative path inside priv, for example
  # "priv/ssl/server.key". For all supported SSL configuration
  # options, see https://hexdocs.pm/plug/Plug.SSL.html#configure/1
  #
  # We also recommend setting `force_ssl` in your endpoint, ensuring
  # no data is ever sent via http, always redirecting to https:
  #
  #     config :octocon, OctoconWeb.Endpoint,
  #       force_ssl: [hsts: true]
  #
  # Check `Plug.SSL` for all available options in `force_ssl`.
end

if config_env() == :prod do
  config :sentry,
    dsn: System.get_env("SENTRY_DSN"),
    environment_name: :prod
end
