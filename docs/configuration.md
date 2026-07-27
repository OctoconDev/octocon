# Interfold configuration reference

This is the developer / architecture reference for how Interfold gets configured. If you are
self-hosting and just want the short list of env vars you must set by hand, jump to the
[README configuration block](../README.md#configuration--integrations) — it links back here
for the deep dive.

> **Audience.** Operators reading this should already understand Docker Compose, Postgres
> roles, and Kestrel HTTPS. Maintainers extending it should already know
> `IOptionsMonitor<T>`, `IHostedLifecycleService`, and the Aspire AppHost model.

## TL;DR

There are four configuration layers. Each row in a layer eventually lands on a strongly-typed
options object inside the API process or on a seeded row inside Postgres.


| Layer                        | Source of truth                             | Where it lives at rest                              | Read by                            |
| ---------------------------- | ------------------------------------------- | --------------------------------------------------- | ---------------------------------- |
| **1. Compile-time defaults** | C# constants and property initialisers      | Source code (`Interfold.Contracts.Configuration.*`) | DI binding helpers                 |
| **2. Operator file**         | `deploy/interfold.bootstrap.json`           | Operator's working copy                             | `Interfold.Bootstrapper` only      |
| **3. Environment variables** | `deploy/.env` (compose-bound) + process env | `OCTOCON_*`, `ASPNETCORE_*`, `Parameters:*`         | API at DI bind time                |
| **4. `internal.secrets`**    | Postgres row `internal.secrets` table       | Inside the application database (default `interfold`, configurable via `BootstrapConfig.postgresDatabase`) | API at startup via `ISecretsStore` |


Layers cascade right-to-left at boot: env binds first, then `SecretsBootstrapService`
overlays values from `internal.secrets` on top of `AuthenticationConfiguration`. The leaf
PFX password takes a separate one-shot Postgres lookup *before* the host is built so Kestrel
can unlock the cert when it binds the HTTPS endpoint.

## Layer 1 — Compile-time defaults

Every option lives on a `sealed class` in `Interfold.Contracts.Configuration`. Each property
has a default chosen for safety, not convenience — if an operator forgets to set anything,
the defaults aim for "loud failure in production, useful behaviour in dev".

Reference:

- `[PersistenceConfiguration](../shared/Interfold.Contracts/Configuration/PersistenceConfiguration.cs)`
— DB mode, keyspace, retry/backoff knobs.
- `[AuthenticationConfiguration](../shared/Interfold.Contracts/Configuration/AuthenticationConfiguration.cs)`
— JWT signing keys, OAuth client IDs, deep-link HMAC secret, encryption pepper, challenge
scheme metadata.
- `[ApiConfiguration](../shared/Interfold.Contracts/Configuration/ApiConfiguration.cs)` —
frontend URLs, deep-link protocol.
- `[StorageConfiguration](../shared/Interfold.Contracts/Configuration/StorageConfiguration.cs)`
— local avatar storage root + public base URL.
- `[SocketConfiguration](../shared/Interfold.Contracts/Configuration/SocketConfiguration.cs)`
— WebSocket batch flush threshold.
- `[ObservabilityConfiguration](../shared/Interfold.Contracts/Configuration/ObservabilityConfiguration.cs)`
— OTLP endpoint.
- `[ClusterConfiguration](../shared/Interfold.Contracts/Configuration/ClusterConfiguration.cs)`
— node group (Primary / Auxiliary / Sidecar).
- `[TestingConfiguration](../shared/Interfold.Contracts/Configuration/TestingConfiguration.cs)`
— test-only gating switches.

DI binding is centralised in
`[ConfigurationServiceCollectionExtensions.AddInterfoldOptions](../infrastructure/Interfold.Infrastructure/DependencyInjection/ConfigurationServiceCollectionExtensions.cs)`.
Each `Apply`* method is the single source of truth for that section's env → options mapping.

## Layer 2 — `interfold.bootstrap.json`

This file is the operator-facing input to the bootstrapper. It is *not* read by the API
directly — its values flow through the bootstrapper into either `secrets.json` (auto-generated
output) or the `internal.secrets` table (seeded by `DatabaseInitPhase`).

Shape lives on `[BootstrapConfig](../tools/Interfold.Bootstrapper/Configuration/BootstrapConfig.cs)`:

```jsonc
{
  "deployment": {
    "outputDir": "./deploy",         // artifact root (relative to cwd)
    "hosts":     [],                 // required, no default — DNS names / IPv4 / IPv6 / CIDR.
                                     // See "Hosts" subsection below for the entry shapes
                                     // and primary-host rule. Examples:
                                     //   ["api.example.com"]            (DNS only)
                                     //   ["192.168.1.42"]               (LAN box, no domain)
                                     //   ["api.example.com","fe80::1"]  (mixed)
                                     //   ["api.example.com","10.0.0.0/8"] (DNS + CIDR scope)
    "rootCaName":"Interfold Root CA",
    "certYears": 5,
    "trustStoreInstall": true,       // add rootCA.crt to system trust store
    "includeWeb": false,             // ship the octocon-web (Kotlin/Wasm UI) container in
                                     // the generated compose? Independent from `webHttps`
                                     // below. Default false ships an API-only stack. Set
                                     // true with `webHttps:false` to ship the wasm UI in
                                     // HTTP-only mode (debugging / external-TLS-proxy
                                     // stacks); `webHttps:true` auto-promotes this to
                                     // true regardless of what's written here.
    "webHttps": false                // terminate TLS at octocon-web (see "webHttps and
                                     // the HTTP->HTTPS redirect" note below)
  },
  "ports": {
    "apiHttp":  5000,
    "apiHttps": 5001,
    "webHttp":  8080,
    "webHttps": 8081
  },
  "scyllaMode": "single",            // "single" | "multi" | "cassandra"
  "apiImage":   "ghcr.io/azyyyyyy/interfold-api:latest",
  "postgresDatabase": "interfold",   // Postgres application DB name; any safe identifier
                                     // matching ^[A-Za-z_][A-Za-z0-9_]{0,62}$. Becomes the
                                     // Database= field on OCTOCON_POSTGRES_CONNECTION and
                                     // the target of DatabaseInitPhase's CREATE DATABASE.
  "clusterName": "InterfoldCluster", // Advertised CQL cluster identity. Lands on
                                     // CASSANDRA_CLUSTER_NAME (Cassandra) and the
                                     // --cluster-name CLI flag (Scylla). Pure metadata,
                                     // visible via `SELECT cluster_name FROM system.local`.
                                     // Allowed: 1..64 chars matching [A-Za-z0-9 ._-].
  "scyllaKeyspace": "nam",           // Per-instance region identity. One of:
                                     // nam | eur | sam | sas | eas | ocn | gdpr.
                                     // Lands on OCTOCON_SCYLLA_KEYSPACE on the API container;
                                     // also picks which regional keyspace this stack's API
                                     // serves (`single`/`cassandra` modes only create one,
                                     // so this and the migration target must agree).
  "apiRuntime": {
    // Three of the four are derivable from `deployment` + `ports` and may be left blank —
    // the bootstrapper fills them at validate-time with values computed from `hosts`,
    // `webHttps`, and the matching `ports.*` slot. The interactive form pre-fills the
    // prompt with the same derived value.
    "callbackBaseUrl":    "",        // empty -> https://{primary host}[:{ports.apiHttps}];
                                     // lands on OCTOCON_AUTH_CALLBACK_BASE_URL. The scheme is
                                     // always `https` because the API container terminates
                                     // HTTPS unconditionally in self-host (Kestrel binds the
                                     // bootstrapper-issued leaf PFX independent of `webHttps`,
                                     // which only governs the web container). The port
                                     // suffix is dropped when `ports.apiHttps` is 443.
                                     // (Primary host = first non-CIDR entry in `hosts`; IPv6
                                     // literals are bracket-wrapped per RFC 3986.)
    "jwtAuthority":       "",        // empty -> https://{primary host}[:{ports.apiHttps}];
                                     // JWT `iss` claim. Same derivation as
                                     // `callbackBaseUrl`.
    "jwtAudience":        "octocon", // JWT `aud` claim; no derivation, just a default.
    "corsAllowedOrigins": []         // empty -> one entry per non-CIDR `hosts` entry of the
                                     // form `{webScheme}://{host}[:{webPort}]`, where
                                     // `webScheme` follows `webHttps` and `webPort` is the
                                     // matching `ports.webHttps` / `ports.webHttp` value (port
                                     // suffix dropped when default for the scheme: 80 for
                                     // http, 443 for https). Joined with ',' for
                                     // OCTOCON_CORS_ALLOWED_ORIGINS. CIDR entries are
                                     // skipped because they have no URL form. An empty list
                                     // at the API would fall back to "allow any origin",
                                     // which the bootstrapper actively prevents in
                                     // production.
  },
  "persistence": {
    // DB-retry strategy + per-request fan-out cap. All four have non-null defaults that
    // match the API's compile-time fallbacks — leaving them at the defaults reproduces
    // pre-bootstrapper behaviour 1:1. ConfigPhase.Validate enforces ranges and the
    // `max >= initial` cross-check.
    "dbRetryAttempts":         3,    // 1..100;     OCTOCON_DB_RETRY_ATTEMPTS
    "dbRetryInitialDelayMs":   100,  // 1..60000;   OCTOCON_DB_RETRY_INITIAL_DELAY_MS
    "dbRetryMaxDelayMs":       1500, // 1..600000;  must be >= dbRetryInitialDelayMs
    "hydrationMaxConcurrency": 8     // 1..1024;    OCTOCON_HYDRATION_MAX_CONCURRENCY
  },
  "cluster": {
    // Node role used by the API for orchestration-aware decisions. Lower-cased on the
    // API side; the bootstrapper validator enforces the three canonical values upfront.
    "nodeGroup": "auxiliary"         // primary | auxiliary | sidecar; OCTOCON_NODE_GROUP
                                     // Fly.io stacks override via FLY_PROCESS_GROUP at
                                     // runtime, which wins over OCTOCON_NODE_GROUP.
  },
  "storage": {
    // Both fields are optional — leaving either empty disables the API's avatar surface
    // entirely (the binder normalises empty -> null, and the avatar service's
    // not-configured check kicks in). When opting in, set both AND add the matching
    // compose bind mount in a compose override; the bootstrapper does not create the
    // directory or wire the mount.
    "avatarStorageRoot": "",         // absolute container path; OCTOCON_AVATAR_STORAGE_ROOT
    "avatarPublicBase":  ""          // public http(s) URL prefix; OCTOCON_AVATAR_PUBLIC_BASE
  },
  "observability": {
    // OTLP gRPC endpoint the API exports traces and metrics to. Empty means the OTLP
    // exporter is not registered (in-process telemetry still works). When set, must
    // parse as an absolute http(s) URI.
    "otlpEndpoint": ""               // e.g. "http://localhost:4317"; OCTOCON_OTLP_ENDPOINT
  },
  "socket": {
    // Nullable int — `null` (the default) means "use the API's compile-time default".
    // The JSON stores literal null rather than 0 so a re-bootstrap of a hand-edited
    // file doesn't accidentally set the threshold to "flush every empty payload".
    "batchBytesThreshold": null      // 1..16777216 when set; OCTOCON_SOCKET_BATCH_BYTES_THRESHOLD
  },
  "oauth": {
    // Per-provider OAuth credentials. Rows are paired (id then secret); each provider
    // needs BOTH halves to register its ASP.NET Core challenge scheme. Leaving a
    // provider's `*ClientId` empty disables that provider entirely (the scheme is
    // skipped at startup) regardless of whether a secret is set.
    "googleClientId":      "1234.apps.googleusercontent.com",  // empty -> provider disabled
    "googleClientSecret":  "...",                              // empty -> row skipped
    "discordClientId":     "",                                 // empty -> provider disabled
    "discordClientSecret": "",                                 // empty -> row skipped
    "appleClientId":       "",                                 // empty -> provider disabled
    "appleClientSecret":   ""                                  // empty -> row skipped
  },
  "backup": {
    // Backup + autostart preferences consumed by the `backup` and `install-service`
    // subcommands. Every field defaults to a "do nothing automatically" stance —
    // operators opt in here, then re-run `install-service` to materialise the matching
    // systemd units. See README.md "Backups" / "systemd integration" for the operator
    // walkthrough.
    "enabled":         false,        // master toggle for the scheduled-backup timer.
                                     //   When false, install-service does NOT enable
                                     //   interfold-backup.timer. The one-shot `backup`
                                     //   subcommand always works regardless.
    "schedule":        "daily",      // systemd OnCalendar= expression. Accepts the
                                     //   shortcuts (hourly|daily|weekly|monthly) plus
                                     //   the full "DOW YYYY-MM-DD HH:MM:SS" form
                                     //   (e.g. "Mon..Fri 03:30"). Validated at install
                                     //   time by `systemd-analyze calendar`.
    "retainCount":     14,           // per-component archive count to retain. After
                                     //   every successful backup the oldest archives
                                     //   (by mtime) are deleted until exactly this
                                     //   many remain. Bounded 1..1000.
    "directory":       "",           // blank -> {outputDir}/backups. Non-blank values
                                     //   must be ABSOLUTE — systemd timer invocations
                                     //   have an unpredictable CWD, so relative paths
                                     //   would not resolve consistently.
    "autostartServer": false         // when true, install-service enables
                                     //   interfold.service so `docker compose up -d`
                                     //   runs on every boot after docker.service.
                                     //   Independent of `enabled`.
  },
  "update": {
    // Docker image update preferences consumed by the `update-images` subcommand and by
    // the systemd OnSuccess= drop-in that chains updates after successful backups. Every
    // field defaults to a "manual updates only, no automatic rollback" stance — flip the
    // toggles you want and re-run `install-service` to materialise the systemd chain.
    // See README.md "Updating images" for the operator walkthrough.
    "enabled":                    false, // master toggle for the systemd chain. When true,
                                         //   install-service writes
                                         //   interfold-backup.service.d/50-chain-update.conf
                                         //   with an OnSuccess=interfold-update.service
                                         //   directive so every successful scheduled backup
                                         //   triggers `update-images`. Requires systemd
                                         //   >= 249 (Ubuntu 22.04+ / Debian 12+); the
                                         //   install phase preflights the version and
                                         //   refuses on older hosts. Manual `update-images`
                                         //   works regardless of this toggle.
    "healthCheckTimeoutSeconds":  180,   // bounded 1..3600. Post-recreate deadline
                                         //   for the pg_isready + nodetool status +
                                         //   /health/ready probes combined. Any tier
                                         //   that doesn't clear its check by the deadline
                                         //   triggers the log-and-stop / auto-restore
                                         //   branch.
    "autoRestoreOnFailure":       false, // when true, a failed health check invokes
                                         //   `restore --force` against the pre-update
                                         //   archives inline. Default false: the phase
                                         //   just prints a copy-pasteable restore command
                                         //   and exits non-zero — operator decides. The
                                         //   CLI --auto-restore flag overrides this
                                         //   per-invocation.
    "recreateOnUpdate":           true,  // when true (default), the phase runs
                                         //   `docker compose up -d` after a pull so
                                         //   compose recreates containers whose image
                                         //   ID moved. Set false only if you want a
                                         //   two-step manual recreate (pull now, `up -d`
                                         //   later during a maintenance window).
    "services":                   []     // empty -> every compose service is pulled +
                                         //   recreated. Non-empty is a whitelist
                                         //   validated against the known compose service
                                         //   names (msg-db, scylla, scylla-*, cassandra,
                                         //   interfold-api, octocon-web). Use to update
                                         //   just the API without touching Postgres or
                                         //   Scylla, for example.
  }
}
```

OAuth **client IDs** are public values that end up in each provider's authorize-redirect URL.
The bootstrapper carries them through as plain Aspire parameters (no masking, no
`internal.secrets` round trip) — `PublishPhase.BuildEnvReplacements` writes them straight
into `deploy/.env` as `GOOGLE_OAUTH_CLIENT_ID` / `DISCORD_OAUTH_CLIENT_ID` /
`APPLE_OAUTH_CLIENT_ID`, which the API container picks up as `OCTOCON_*_OAUTH_CLIENT_ID`
via `InterfoldAppHost.ConfigureApiSelfHostEnv`. The matching client **secrets** live in
`internal.secrets` (seeded by `DatabaseInitPhase`) and are patched onto
`AuthenticationConfiguration` by `SecretsBootstrapService` at API startup — they never
appear in `.env`.

First-time operators don't need to hand-author this file — running `interfold-bootstrap` on
a real TTY without an existing `interfold.bootstrap.json` drops into a Spectre.Console
navigable form: every field on `BootstrapConfig` is shown as a menu row with its current
value next to its label, grouped under ten section headers (Deployment / Ports /
Database / API / Cluster & telemetry / Storage / Performance tuning / OAuth credentials /
Backup & autostart / Updates).
The operator arrow-keys between rows and presses Enter to edit any field (inline validation
re-prompts on bad input, OAuth client secrets are masked in both the editor echo and the
menu row; client IDs are shown verbatim because they're public), then chooses `Confirm and
save` to write the JSON. The four derivable `apiRuntime` rows pre-fill their menu display
and prompt default with the value `ConfigPhase.ResolveDerivedDefaults` computes from
`deployment` — operators can press Enter to accept or type to override, and either way the
bootstrapper persists the resolved value. The four "disabled when blank" rows (avatar
storage root + public base, OTLP endpoint, socket batch flush threshold) render an
`<empty>` / `<default>` marker in the menu when unset, so the unset-vs-set distinction is
visible at a glance; leaving them blank reproduces the pre-bootstrapper "env var unset"
behaviour 1:1. There is no separate walkthrough phase: experienced operators jump straight
to the rows they care about and Confirm; first-time operators just Enter every row
top-to-bottom. The bootstrapper writes the resulting JSON to the path above on
confirmation; `--non-interactive` and `--config <path>` still bypass the form for
unattended runs.

### Hosts (`deployment.hosts`)

The `hosts` list is the single source of truth for where the deployed API will be
reachable. The field is required (no shipped default placeholder), and each entry is one
of the following shapes:

| Shape           | Example                | Used as leaf SAN? | Used as Name Constraints subtree? | URL-primary eligible? |
| --------------- | ---------------------- | ----------------- | --------------------------------- | --------------------- |
| DNS name        | `api.example.com`      | yes (`dNSName`)   | yes (`dNSName`)                   | yes                   |
| DNS wildcard    | `*.example.com`        | yes (`dNSName`)   | yes (suffix only)                 | no                    |
| IPv4 literal    | `192.168.1.42`         | yes (`iPAddress`) | yes (`iPAddress` `/32`)           | yes                   |
| IPv6 literal    | `fe80::1`              | yes (`iPAddress`) | yes (`iPAddress` `/128`)          | yes                   |
| IPv4 CIDR       | `192.168.1.0/24`       | no                | yes (`iPAddress` + explicit mask) | no                    |
| IPv6 CIDR       | `fe80::/64`            | no                | yes (`iPAddress` + explicit mask) | no                    |

The **primary host** is the first non-CIDR entry. It seeds the leaf cert subject CN, the
nginx `server_name`, and the derived `apiRuntime.callbackBaseUrl` /
`apiRuntime.jwtAuthority` URLs (always `https` for the API in self-host, with the
`:{ports.apiHttps}` suffix dropped only when the operator picked 443; IPv6 literals are
bracket-wrapped per RFC 3986 §3.2.2). Wildcard DNS entries cannot be the primary because
`*.example.com` is not a single host the leaf cert can serve — list the concrete primary
alongside the wildcard.

CIDR entries are useful when you want the root CA's Name Constraints scope to cover an
entire subnet (for example a LAN where individual leaf certs are minted later) without
listing every host. CIDR entries with host bits set beyond the mask (`192.168.1.42/24`)
are rejected at parse time with a fix-it message — the operator must canonicalise the
network address (`192.168.1.0/24`) or pin a single host (`192.168.1.42/32`).

**LAN-only quick start (no DNS at all):**

```jsonc
{
  "deployment": {
    "hosts":     ["192.168.1.42"],   // the box's static LAN IP
    "rootCaName":"Interfold Root CA",
    "certYears": 5,
    "webHttps":  true
  }
}
```

The leaf cert gets an `iPAddress` SAN for `192.168.1.42`, the root CA's Name Constraints
pin to the same `/32`, and devices on the LAN that install the root CA validate
`https://192.168.1.42/` cleanly.

> **Shipping the `octocon-web` (wasm UI) container.** Two independent toggles under
> `deployment` control whether the wasm UI ends up in the generated compose:
>
> - `deployment.includeWeb` (default `false`) — when `true`, ship the `octocon-web`
>   container in HTTP-only mode. The upstream image (`ghcr.io/azyyyyyy/octocon-wasm:latest`)
>   listens on `:8080`, so the bootstrapper maps `ports.webHttp` / `ports.webHttps` onto
>   `:8080` and emits an HTTP healthcheck. No leaf cert or nginx envsubst template gets
>   bind-mounted in this mode — it's intended for local debugging and for stacks fronted
>   by an external TLS-terminating proxy.
> - `deployment.webHttps` (default `false`) — when `true`, terminate TLS at `octocon-web`
>   itself (the path the rest of this section documents). Setting this implicitly forces
>   `includeWeb` on (TLS termination requires the container that performs it), so
>   operators who only want TLS don't need to flip both.
>
> Combinations:
>
> | `includeWeb` | `webHttps` | Result                                            |
> | :----------: | :--------: | :------------------------------------------------ |
> | `false`      | `false`    | API-only stack (default).                         |
> | `true`       | `false`    | Wasm UI shipped HTTP-only (debug / external TLS). |
> | `false`      | `true`     | Wasm UI shipped with TLS termination at nginx.    |
> | `true`       | `true`     | Same as the row above (`includeWeb` is implied).  |
>
> **`webHttps` and the HTTP→HTTPS redirect.** When `deployment.webHttps` is `true`, the
> `octocon-web` container terminates TLS on its internal `:443` listener and answers
> plaintext `:80` requests with a `301` to the HTTPS variant. The redirect's `Location`
> header is stitched together inside nginx as `https://$host${NGINX_HTTPS_PORT_SUFFIX}…`,
> where `NGINX_HTTPS_PORT_SUFFIX` is set by `InterfoldAppHost.Configure` to
> `:{ports.webHttps}` (or empty when the operator picked `443`). This is the bit that lets
> the default port pair (`webHttp`/`webHttps` = `8080`/`8081`) actually work end-to-end —
> without it the browser would follow the 301 to `https://<host>/`, resolve the missing
> port to `:443`, and hit a port that the bootstrapper hasn't bound. Operators fronting
> the stack with a reverse proxy that exposes a different public port should override
> `ports.webHttps` to that public port (or move the reverse proxy off `webHttp`/`webHttps`
> entirely and skip `octocon-web`'s HTTPS termination).

> **Interactive auto-default.** When the bootstrapper runs interactively on a fresh box
> (no `interfold.bootstrap.json` yet) it pre-fills the *Public host(s)* row with the
> device's primary unicast IP — enumerated via `NetworkInterface.GetAllNetworkInterfaces()`
> and filtered to skip loopback, tunnels, link-local, APIPA, and named virtual bridges
> (Docker, k8s overlays, Hyper-V switches, WireGuard / Tailscale tunnels). The operator
> hits Enter to accept or types a replacement. The non-interactive path (`--non-interactive`
> with a JSON file) does **not** consult the detector — a file with an empty `hosts` list
> still fails fast with a clear validation error, by design.

> **mDNS preflight for `.local` names (Linux only).** The bootstrapper runs a two-tier
> check whenever the finalised `deployment.hosts` list contains a `.local` entry:
>
> - **Pre-prompt banner (interactive fresh-config only).** Before the hosts row appears,
>   the bootstrapper detects the device's short hostname, qualifies it as
>   `{hostname}.local`, and probes whether it resolves via `getent hosts` (which
>   traverses `nsswitch.conf` → `mdns_minimal` → avahi). If it does, the qualified name
>   joins the auto-default alongside the primary IP. If it doesn't, a banner explains
>   that mDNS is unavailable and offers to install `avahi-daemon` + the platform's NSS
>   mdns module (`libnss-mdns` on Debian/Ubuntu, `nss-mdns` on Fedora/RHEL). Decline the
>   offer and the `.local` name is simply omitted from the pre-fill — the row is still
>   editable so the operator can type any host they like.
> - **Post-fill safety gate (all `bootstrap` runs).** After the hosts list is finalised
>   (either by the interactive prompt or loaded from JSON), every `.local` entry is
>   re-probed. Unresolvable ones are removed from `deployment.hosts` with a warning
>   naming the specific hosts + a copy-pasteable install hint, and **the current
>   `bootstrap` run continues to completion** with the reduced list. It never halts on
>   this — the mutation is re-persisted so subsequent runs see the pruned list without
>   re-emitting the warning. Re-running `bootstrap` after installing mDNS is *optional
>   recovery* to restore the stripped entries, not a required next step.
>
> `--non-interactive` skips the pre-prompt banner (there's no operator to talk to) but
> the post-fill gate still runs and applies the same strip-and-continue behaviour to
> `.local` entries in the supplied config. Non-Linux platforms short-circuit both tiers
> because `getent`'s exit-code contract doesn't translate to Windows / macOS resolvers.
>
> To enable mDNS ahead of time so the strip never fires:
>
> ```bash
> # Debian / Ubuntu
> sudo apt-get install -y avahi-daemon libnss-mdns && sudo systemctl enable --now avahi-daemon
>
> # Fedora / RHEL
> sudo dnf install -y avahi nss-mdns && sudo systemctl enable --now avahi-daemon
> ```

## Layer 3 — Environment variables

Two flavours:

- **Compose-bound** — `deploy/.env` is templated by `PublishPhase.BuildEnvReplacements` from
the AppHost's `Parameters:`* declarations. Operators usually never touch `.env` by hand;
rerunning the bootstrapper rewrites it.
- **Operator-bound** — variables the bootstrapper does *not* manage. Operators export them
via systemd, a sibling `.env.local`, or whatever orchestration they use. See the
README's [Configuration & integrations](../README.md#configuration--integrations) for the
short operator-facing list.

### Bootstrapper-installed systemd units

`interfold-bootstrap install-service` materialises four systemd units to
`/etc/systemd/system/` (overridable via `--systemd-unit-dir`, used by integration tests).
All four are rendered from templates embedded in the bootstrapper binary; see
`tools/Interfold.Bootstrapper/Phases/SystemdTemplates/` for the source.

| Unit                       | Type                       | What it does |
| -------------------------- | -------------------------- | ------------ |
| `interfold.service`        | `oneshot` `RemainAfterExit=yes` | Brings the compose stack up via `/usr/bin/docker compose -f {outputDir}/docker-compose.yaml up -d` after `docker.service` on boot. Deliberately does NOT shell out to `interfold-bootstrap up` — that would re-run the 5-minute `/health/ready` wait inside systemd's boot critical path. Compose's own restart policy + the API container's healthcheck handle steady-state recovery. |
| `interfold-backup.service` | `oneshot`                  | Runs `interfold-bootstrap backup --config {configPath} --output-dir {outputDir} --component all`. Inherits the bootstrapper's `phase=...` log line format. Operators add drop-in overrides via `/etc/systemd/system/interfold-backup.service.d/*.conf`; the bootstrapper never edits drop-ins on rerun. |
| `interfold-backup.timer`   | `timer`                    | Fires `interfold-backup.service` on `OnCalendar={config.backup.schedule}` with `Persistent=true` so a missed run (host powered off at the scheduled time) fires on next boot. |
| `interfold-update.service` | `oneshot`                  | Runs `interfold-bootstrap update-images --config {configPath} --output-dir {outputDir}`. Always rendered so manual invocations always have a target service; only the `OnSuccess=` drop-in that fires it from the backup schedule is conditional on `config.update.enabled`. Never enabled independently — the drop-in is what schedules it. |

Conditional drop-in — written only when `config.update.enabled=true`:

| Path                                                                    | Contents                                             |
| ----------------------------------------------------------------------- | ---------------------------------------------------- |
| `/etc/systemd/system/interfold-backup.service.d/50-chain-update.conf`   | `[Unit]\nOnSuccess=interfold-update.service`         |

The drop-in makes a successful `interfold-backup.service` run trigger
`interfold-update.service`. Rendered idempotently: flipping `config.update.enabled` from
`true` to `false` and re-running `install-service` deletes the drop-in (the update
`.service` file stays for manual use). The `50-` numeric prefix leaves headroom for
operator-managed higher-priority drop-ins to override the chain via a
`90-local.conf` sibling.

Enable/disable contract:

- `install-service --enable-autostart` runs `systemctl enable --now interfold.service`
  after writing the unit. The CLI flag defaults to `config.backup.autostartServer`, so
  operators who set that in `interfold.bootstrap.json` get the boot service enabled on a
  plain `install-service` invocation.
- `install-service --enable-backup-timer` runs `systemctl enable --now interfold-backup.timer`.
  Defaults to `config.backup.enabled`.
- `interfold-update.service` is **never** enabled directly — the `OnSuccess=` drop-in is
  what schedules it. Enabling it manually would create a boot-time update pass, which is
  not the design goal.
- Both flags require `systemctl` to be on PATH; if it isn't (Windows / macOS dev box,
  unprivileged container) the units still get written but no enable-step runs and the
  log says `systemctl not on PATH; units written but not enabled`.
- `config.update.enabled=true` requires systemd >= 249 (the minimum version for the
  `OnSuccess=` directive). The install phase parses `systemctl --version`, fails fast
  on older hosts with a clear error naming Ubuntu 22.04 / Debian 12 as the minimum, and
  skips the preflight entirely when update is disabled.

Validation happens at install time: `systemd-analyze verify` runs against each rendered
unit and `systemd-analyze calendar` against the schedule string. Either failing aborts
the install with the analyzer's output — the unit files stay on disk so the operator can
inspect and edit them, but no `daemon-reload` happens.

### Full env inventory

Variables marked **(bootstrapper-managed)** are written into `deploy/.env` by the
publish phase. Variables marked **(operator)** must be supplied by hand.

#### Persistence


| Env var                             | Default                                           | Notes                                                                                                  |
| ----------------------------------- | ------------------------------------------------- | ------------------------------------------------------------------------------------------------------ |
| `OCTOCON_PERSISTENCE`               | `scylla-postgres`                                 | `scylla-postgres` or `inmemory`. (bootstrapper-managed)                                                |
| `OCTOCON_POSTGRES_CONNECTION`       | localhost fallback                                | Built from `Parameters:postgres-*` (including `Parameters:postgres-db`, default `interfold`, sourced from `BootstrapConfig.postgresDatabase`). (bootstrapper-managed) |
| `OCTOCON_SCYLLA_KEYSPACE`           | `nam`                                             | Per-instance region identity. **Single source.** No store fallback. Sourced from `BootstrapConfig.scyllaKeyspace` via Aspire parameter `scylla-keyspace`; restricted to the seven canonical regional values (`nam`/`eur`/`sam`/`sas`/`eas`/`ocn`/`gdpr`). (bootstrapper-managed) |
| `OCTOCON_SINGLE_SCYLLA_INSTANCE`    | `true` (`single`/`cassandra`) / `false` (`multi`) | Whether the migration service creates all regional keyspaces or just one. (bootstrapper-managed)       |
| `OCTOCON_DB_RETRY_ATTEMPTS`         | `3`                                               | Sourced from `BootstrapConfig.persistence.dbRetryAttempts` via Aspire parameter `db-retry-attempts`; bounded 1..100 by `ConfigPhase.Validate`. (bootstrapper-managed) |
| `OCTOCON_DB_RETRY_INITIAL_DELAY_MS` | `100`                                             | Sourced from `BootstrapConfig.persistence.dbRetryInitialDelayMs` via Aspire parameter `db-retry-initial-delay-ms`; bounded 1..60000 and must be `<= dbRetryMaxDelayMs`. (bootstrapper-managed) |
| `OCTOCON_DB_RETRY_MAX_DELAY_MS`     | `1500`                                            | Sourced from `BootstrapConfig.persistence.dbRetryMaxDelayMs` via Aspire parameter `db-retry-max-delay-ms`; bounded 1..600000 and must be `>= dbRetryInitialDelayMs`. (bootstrapper-managed) |
| `OCTOCON_HYDRATION_MAX_CONCURRENCY` | `8`                                               | Sourced from `BootstrapConfig.persistence.hydrationMaxConcurrency` via Aspire parameter `hydration-max-concurrency`; bounded 1..1024. (bootstrapper-managed) |

`OCTOCON_COMPATIBILITY_MODE` was a "Postgres isn't reachable" escape hatch that forced
idempotency + token revocation into in-memory stores. It's been removed — Postgres is now
a hard dependency (`SecretsBootstrapService` requires `ISecretsStore` to load the
encryption pepper, and the API refuses to boot without it). If the value is still in your
`.env` it's a dead row — the API no longer reads it.


#### Authentication / OAuth


| Env var                               | Default         | Notes                                                                                                                                                 |
| ------------------------------------- | --------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------- |
| `OCTOCON_AUTH_CALLBACK_BASE_URL`      | *empty*         | Base URL the API's OAuth callbacks redirect to. Sourced from `BootstrapConfig.apiRuntime.callbackBaseUrl` via Aspire parameter `oauth-callback-base-url`; defaults derive to `https://{primary host}[:{ports.apiHttps}]` (first non-CIDR entry of `deployment.hosts`, always `https` because the API container terminates HTTPS unconditionally in self-host, port suffix omitted when `ports.apiHttps` is 443; IPv6 literals bracket-wrapped) when the operator leaves the field blank. (bootstrapper-managed) |
| `OCTOCON_JWT_AUTHORITY`               | `octocon-local` | JWT `iss` claim. Sourced from `BootstrapConfig.apiRuntime.jwtAuthority` via Aspire parameter `jwt-authority`; derives the same way as `OCTOCON_AUTH_CALLBACK_BASE_URL`. The `octocon-local` default applies only to dev / non-bootstrapped runs. (bootstrapper-managed) |
| `OCTOCON_JWT_AUDIENCE`                | `octocon`       | JWT `aud` claim. Sourced from `BootstrapConfig.apiRuntime.jwtAudience` via Aspire parameter `jwt-audience`. Bound into `AuthenticationConfiguration.JwtAudience` by `ConfigurationServiceCollectionExtensions.ApplyAuthentication` (previously documented as bound but the binding was missing; fixed alongside the bootstrapper wire-up). (bootstrapper-managed) |
| `OCTOCON_GOOGLE_OAUTH_CLIENT_ID`      | *empty*         | Sourced from `BootstrapConfig.oauth.googleClientId` via Aspire parameter `google-oauth-client-id`; empty value disables the Google scheme. (bootstrapper-managed) |
| `OCTOCON_DISCORD_OAUTH_CLIENT_ID`     | *empty*         | Same handling, sourced from `oauth.discordClientId`. (bootstrapper-managed)                                                                           |
| `OCTOCON_APPLE_OAUTH_CLIENT_ID`       | *empty*         | Same handling, sourced from `oauth.appleClientId`. (bootstrapper-managed)                                                                             |
| `OCTOCON_GOOGLE_OAUTH_CLIENT_SECRET`  | *null*          | Placeholder only; overwritten at startup from `internal.secrets:oauth:google:client_secret`. **Do not rely on the env value.** (bootstrapper-managed) |
| `OCTOCON_DISCORD_OAUTH_CLIENT_SECRET` | *null*          | Same handling. (bootstrapper-managed)                                                                                                                 |
| `OCTOCON_APPLE_OAUTH_CLIENT_SECRET`   | *null*          | Same handling. (bootstrapper-managed)                                                                                                                 |


Each provider's authorize URL, ASP.NET Core challenge scheme name, and static challenge
query parameters (scopes / `response_type` / `response_mode`) are baked into
`OAuthChallengeServiceCollectionExtensions` as constants — every provider serves a single
global URL and the scopes are functionally tied to the data the API's callback handlers
extract, so neither can move without a code change. The scheme is only registered when
the matching `OCTOCON_*_OAUTH_CLIENT_ID` is set; leaving the client ID empty disables the
provider (`/auth/<provider>` falls through to 403). The client ID itself is injected
into the redirect URL by the scheme registration, so operators never have to thread it
through any other env var.

Notably **absent** (intentionally — these moved to `internal.secrets`):

- `OCTOCON_AUTH_RSA_`* / `OCTOCON_AUTH_RSA_*_FILE`
- `OCTOCON_AUTH_EC_*` / `OCTOCON_AUTH_EC_*_FILE`
- `OCTOCON_AUTH_DEEP_LINK_SECRET`
- `OCTOCON_ENCRYPTION_PEPPER` — now strictly store-resident under `encryption:pepper`; `SecretsBootstrapService` refuses to start the API if the row is missing.
- `ASPNETCORE_Kestrel__Certificates__Default__Password`

Removed in favour of in-code constants:

- `OCTOCON_AUTH_CHALLENGE_{GOOGLE,DISCORD,APPLE}_SCHEME` (scheme name is a fixed ASP.NET Core registration key)
- `OCTOCON_AUTH_CHALLENGE_{GOOGLE,DISCORD,APPLE}_ENDPOINT` (provider authorize URLs are stable, single-region values)
- `OCTOCON_AUTH_CHALLENGE_{GOOGLE,DISCORD,APPLE}_PARAMS` (scopes + `response_type` + `response_mode` are tied to what the callback handlers consume — changing them needs a code change)

If any of these are still set in your `.env`, they are dead values — the API no longer
reads them.

#### CORS

| Env var                        | Default                | Notes                                                                                                                                                                                              |
| ------------------------------ | ---------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `OCTOCON_CORS_ALLOWED_ORIGINS` | *empty* (= allow any in dev only) | Comma-separated allow-list of origin URLs. The API still falls back to "allow any origin" when this is unset/empty, but the bootstrapper never emits a stack with an unset value — `BootstrapConfig.apiRuntime.corsAllowedOrigins` defaults to one `{scheme}://host` entry per non-CIDR `deployment.hosts` entry (joined with `,`), routed through Aspire parameter `cors-allowed-origins`. Operators that want a different allow-list edit the list in the interactive form or the JSON. (bootstrapper-managed) |

`OCTOCON_FRONTEND`, `OCTOCON_BETA_FRONTEND`, and `OCTOCON_DEEPLINK_ADDRESS` have all been
removed. Their CORS allow-list use moved to `OCTOCON_CORS_ALLOWED_ORIGINS`; their
post-OAuth redirect-base use went away with the new client contract: clients are now
responsible for passing `redirect_uri` on the initial `GET /auth/{provider}` (or
`GET /auth/link/{provider}`), and the API surfaces a `400 missing_redirect_uri` instead
of falling back to a server-configured default. If any of the three are still set in
your `.env`, they are dead values — the API no longer reads them.


#### Storage


| Env var                       | Default | Notes      |
| ----------------------------- | ------- | ---------- |
| `OCTOCON_AVATAR_STORAGE_ROOT` | *empty* (= avatar storage disabled) | Container-side absolute path the API writes uploaded avatars to. Sourced from `BootstrapConfig.storage.avatarStorageRoot` via Aspire parameter `avatar-storage-root`; empty value is normalised to `null` by `ApplyStorage` so the API's not-configured branch still fires. The bootstrapper validates the value is an absolute path; operators that opt in are responsible for adding the matching compose bind mount. (bootstrapper-managed) |
| `OCTOCON_AVATAR_PUBLIC_BASE`  | *empty* (= avatar storage disabled) | Public URL prefix the API uses to construct avatar URLs in responses (e.g. `https://cdn.example.com/avatars/`). Sourced from `BootstrapConfig.storage.avatarPublicBase` via Aspire parameter `avatar-public-base`; empty normalised to `null`. Non-empty values must parse as absolute http(s) URLs. (bootstrapper-managed) |


#### Observability


| Env var                 | Default | Notes                                                        |
| ----------------------- | ------- | ------------------------------------------------------------ |
| `OCTOCON_OTLP_ENDPOINT` | *empty* (= OTLP exporter not registered) | gRPC OTLP endpoint, e.g. `http://localhost:4317`. Sourced from `BootstrapConfig.observability.otlpEndpoint` via Aspire parameter `otlp-endpoint`; empty normalised to `null` by `ApplyObservability` so the OTLP exporter is not registered. Non-empty values must parse as absolute http(s) URIs. (bootstrapper-managed) |


#### Cluster


| Env var              | Default     | Notes                                             |
| -------------------- | ----------- | ------------------------------------------------- |
| `FLY_PROCESS_GROUP`  | *null*      | Fly.io automatic. Wins over `OCTOCON_NODE_GROUP`. |
| `OCTOCON_NODE_GROUP` | `auxiliary` | Sourced from `BootstrapConfig.cluster.nodeGroup` via Aspire parameter `node-group`; restricted to `primary` / `auxiliary` / `sidecar` by `ConfigPhase.Validate` (lower-cased on read by `ApplyCluster`). (bootstrapper-managed) |


#### Socket


| Env var                                | Default                       | Notes      |
| -------------------------------------- | ----------------------------- | ---------- |
| `OCTOCON_SOCKET_BATCH_BYTES_THRESHOLD` | *empty* (= API uses built-in default) | Bytes threshold the API flushes a batched WebSocket payload at. Sourced from `BootstrapConfig.socket.batchBytesThreshold` (nullable int) via Aspire parameter `socket-batch-bytes-threshold`; empty/null serialises as the empty string and `ApplySocket`'s `TryParseInt` returns `null` so the API's compile-time default still applies. When set, bounded 1..16 MiB. (bootstrapper-managed) |


#### Kestrel (self-host)


| Env var                                           | Default           | Notes                                                                                                              |
| ------------------------------------------------- | ----------------- | ------------------------------------------------------------------------------------------------------------------ |
| `ASPNETCORE_HTTP_PORTS`                           | `5100`            | Set by AppHost from `Ports:api-container-http`. (bootstrapper-managed)                                              |
| `ASPNETCORE_HTTPS_PORTS`                          | `5101`            | Set by AppHost from `Ports:api-container-https`. (bootstrapper-managed)                                             |
| `ASPNETCORE_Kestrel__Certificates__Default__Path` | `/certs/leaf.pfx` | Set by AppHost. (bootstrapper-managed)                                                                             |
| *password is **not** an env var*                  | —                 | Fetched from `internal.secrets:certs:leaf_pfx_password` before Kestrel binds. See [boot ordering](#boot-ordering). |

The API image itself (`/Dockerfile`) does not pin its listening ports — AppHost owns them
end-to-end via the env vars above, plus the matching compose `targetPort` and the
`curl http://localhost:<port>/health/ready` healthcheck. Operators who want to run the
container standalone (outside AppHost) need to set `ASPNETCORE_URLS` themselves; otherwise
ASP.NET Core falls through to its built-in default of `http://+:8080`.


#### Trust artefact paths

| Env var                                  | Default                       | Notes                                                                                                                                                                                                                                                                                              |
| ---------------------------------------- | ----------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `OCTOCON_TRUST_ROOT_CA_PATH`             | `/certs/rootCA.crt`           | Path inside the API container to the PEM-encoded root CA. Served at `/.well-known/interfold-root-ca.{crt,pem}` by `TrustController`. Empty/missing (dev `aspire run` with no `/certs` bind mount) makes the routes 404 instead of erroring. (bootstrapper-managed)                                  |
| `OCTOCON_TRUST_ROOT_CA_FINGERPRINT_PATH` | `/certs/rootCA.sha256.txt`    | Path to the SHA-256 fingerprint sidecar (uppercase colon-hex). Served at `/.well-known/interfold-root-ca.sha256` and reused as the HTTP `ETag` on the cert routes so `rotate-certs` invalidates downstream caches automatically. Empty drops the ETag and 404s the .sha256 route. (bootstrapper-managed) |

See [Trust distribution](#trust-distribution) below for the fetch-and-verify recipe,
the `bootstrap show-trust` command, the Name Constraints invariant, and the
unsupported-clients list.


#### Testing


| Env var                              | Default     | Notes       |
| ------------------------------------ | ----------- | ----------- |
| `OCTOCON_RUN_API_INTEGRATION`        | `false`     | (test-only) |
| `OCTOCON_RUN_LIVE_INTEGRATION`       | `false`     | (test-only) |
| `OCTOCON_TEST_SCYLLA_CONTACT_POINTS` | `127.0.0.1` | (test-only) |
| `OCTOCON_TEST_SCYLLA_USERNAME`       | `cassandra` | (test-only) |
| `OCTOCON_TEST_SCYLLA_PASSWORD`       | `cassandra` | (test-only) |
| `OCTOCON_TEST_REGION`                | `nam`       | (test-only) |


## Trust distribution

The bootstrapper issues its own root CA and signs every leaf cert with it. Devices that
don't already trust the root must install it before they can talk TLS to the API; the
combination of a `/.well-known/` download surface, a SHA-256 fingerprint OOB channel,
and a critical Name Constraints extension on the root cap the blast radius of both
"untrusted certificate" UX failures and a worst-case CA-key compromise.

### Artefacts (`deploy/certs/`)

| File                  | Purpose                                                                                                  | Mode |
| --------------------- | -------------------------------------------------------------------------------------------------------- | ---- |
| `rootCA.crt`          | Public root CA cert (PEM). Bind-mounted into the API at `/certs/rootCA.crt`.                             | 0644 |
| `rootCA.key`          | CA private key (PKCS#8 PEM). Never leaves the host; the bind mount is RO and the API process can't read it because the file is owned-read-only. | 0600 |
| `rootCA.sha256.txt`   | SHA-256 fingerprint of `rootCA.crt` in uppercase colon-hex (matches `openssl x509 -fingerprint -sha256`). | 0644 |
| `leaf.crt` / `leaf.key` / `leaf.pfx` | Leaf cert + key. PFX password lives in `internal.secrets:certs:leaf_pfx_password`. | 0644 |

### Endpoints (`/.well-known/interfold-root-ca.*`)

`[TrustController](../apis/Interfold.Ops.Api/Controllers/TrustController.cs)` serves a
hard-coded allowlist of three routes off the IANA `.well-known` prefix:

| Route                              | Content-Type                                                                              | Body                                                |
| ---------------------------------- | ----------------------------------------------------------------------------------------- | --------------------------------------------------- |
| `interfold-root-ca.crt`            | `application/pkix-cert` (default) / `application/x-x509-ca-cert` (when `Accept` opts in)  | Root CA in DER                                      |
| `interfold-root-ca.pem`            | `application/x-pem-file`                                                                  | Root CA in PEM (the verbatim on-disk bytes)         |
| `interfold-root-ca.sha256`         | `text/plain; charset=utf-8`                                                               | One line: SHA-256 fingerprint in uppercase colon-hex |

All three return `Cache-Control: public, max-age=60, must-revalidate` and an `ETag`
derived from the fingerprint file. `rotate-certs` rewrites the fingerprint, which
changes the ETag, which invalidates downstream caches on the next conditional request.

Returns 404 on every route when the bootstrapper hasn't supplied trust paths (dev mode
with no `/certs` bind mount, or a deployment that explicitly disables them). Hardcoded
allowlist: no user input ever joins a file path, `rootCA.key` is not on the allowlist,
and the file's 0600 owner-only mode means even a path-traversal regression here cannot
read it from the API process.

### Operator fingerprint surface

`bootstrap show-trust` is a read-only command that loads the on-disk root CA and prints:

```text
Root CA:     Interfold Root CA
  Path:        /…/deploy/certs/rootCA.crt
  SHA-256:     AA:BB:CC:…:99
  Not after:   2030-01-01 12:00:00 UTC
  Distribute:  curl -fSL http://<host>:5000/.well-known/interfold-root-ca.crt -o rootCA.crt
  Verify:      openssl x509 -in rootCA.crt -noout -fingerprint -sha256
               (compare the printed SHA256 Fingerprint to the value above)
```

The same block prints at the end of every `bootstrap` / `bootstrap rotate-certs`
invocation. `show-trust` short-circuits in `Orchestrator.RunAsync` before any other
phase runs, so it's safe to invoke from an ops jumphost without an
`interfold.bootstrap.json` and won't touch the running stack.

### End-user fetch-and-verify recipe

The whole point of the fingerprint is to let an end user verify the cert they just
downloaded came from the operator and wasn't substituted by a network attacker:

```bash
# 1. Operator broadcasts the fingerprint via Slack / email / Keybase / etc.
EXPECTED="AA:BB:CC:DD:…:99"

# 2. User fetches the cert. Plain HTTP is fine here — the SHA-256 is what makes this safe.
curl -fSL http://api.example.com:5000/.well-known/interfold-root-ca.crt -o rootCA.crt

# 3. User computes the fingerprint and compares character-for-character.
openssl x509 -in rootCA.crt -noout -fingerprint -sha256
# SHA256 Fingerprint=AA:BB:CC:DD:…:99

# 4. ONLY after the fingerprints match: install into the device trust store.
sudo cp rootCA.crt /usr/local/share/ca-certificates/interfold-root-ca.crt
sudo update-ca-certificates
```

The OOB channel (Slack / email / Signal) is load-bearing. This is the standard
**trust-on-first-use (TOFU)** bootstrap pattern — the same shape SSH host-key
fingerprints, GPG key-signing parties, and Signal safety numbers all use: the in-band
download supplies the bytes, the out-of-band channel supplies the *authenticity
binding*. Strip the OOB step and the scheme reduces to a "leap of faith" — an attacker
who can MITM the plain-HTTP request can substitute a different root CA and the user has
no in-band way to detect it. TOFU explicitly does not defend against an active attacker
present at first contact ([RFC 7435 §1.1][rfc7435-1-1]); the OOB fingerprint comparison
is what closes that gap. [RFC 4949][rfc4949] formalises both terms — its "out-of-band"
tutorial calls out distributing "a root key" as the canonical example, which is
literally what we're doing here.

[rfc7435-1-1]: https://www.rfc-editor.org/rfc/rfc7435#section-1.1
[rfc4949]: https://www.rfc-editor.org/rfc/rfc4949

### Name Constraints invariant

The root CA carries a critical Name Constraints extension (RFC 5280 §4.2.1.10) whose
`permittedSubtrees` is the operator's `deployment.hosts` list:

- **DNS** entries (`api.example.com`, `*.example.com`) emit a `dNSName` permittedSubtree.
  Wildcard entries collapse to their suffix because dNSName subtree semantics already
  cover sub-labels and `*` is not a legal IA5String value.
- **IPv4 / IPv6** literals emit an `iPAddress` permittedSubtree with an all-ones mask
  (`/32` for IPv4, `/128` for IPv6) — the leaf cert carries a matching `iPAddress` SAN so
  a client browsing `https://192.168.1.42` validates cleanly.
- **CIDR** entries (`192.168.1.0/24`, `fe80::/64`) emit an `iPAddress` permittedSubtree
  with the operator-supplied prefix. CIDR widens the *root CA's permitted scope* without
  itself appearing on any leaf SAN — the operator must still list the specific hosts they
  want the leaf cert to serve.

A leaked CA private key therefore cannot mint a trusted cert for any host outside the
operator's configured set — every device that installed this root will reject the
impostor at chain validation. The blast radius of a key compromise is the operator's
host set, not the public internet.

Trade-off: a handful of older or embedded TLS stacks don't support Name Constraints
and will reject the leaf chain entirely. Known unsupported clients:

- Android < 7
- Java < 8u101
- Several embedded TLS stacks (consult device docs)

Modern curl, browsers, OpenSSL ≥ 1.0.1, and .NET ≥ 6 handle Name Constraints
correctly. There is no opt-out: the constraint is critical so a non-supporting client
would otherwise silently treat the extension as absent, defeating the security
property entirely.

### Rotation

`bootstrap rotate-certs` regenerates the root CA, leaf, and fingerprint. Operator
consequences:

1. Every client device that previously installed the old root must install the new
   one. `CertificatePhase` prints a prominent two-line warning to that effect when it
   runs in rotate mode, immediately after the trust-info block so the new fingerprint
   is right above the call-to-action.
2. Cached `.well-known` responses pick up the new bytes on the next conditional
   request because the ETag changes with the fingerprint.
3. The CA private key permissions (0600) are re-applied even on idempotent reruns,
   so upgrading from a pre-hardening install backfills the lockdown without needing
   an explicit rotate. `rootCA.sha256.txt` is similarly backfilled on first run after
   upgrading, with no regeneration of the CA.

## Layer 4 — `internal.secrets`

The `internal.secrets` table is the durable, in-cluster source of truth for everything
sensitive that the API needs at runtime. It is:

- created by `DatabaseInitPhase` inside the bootstrapper, owned by the `<app>_admin` role;
- read-only granted to the app `interfold` user;
- seeded once on first bootstrap and re-seeded whenever the bootstrapper runs (writes are
idempotent — empty values are skipped to avoid clobbering operator-set rows).

Row inventory (see `[SeedKeys.cs](../shared/Interfold.DatabaseBootstrap/SeedKeys.cs)`):


| Key                           | Origin                                      | Consumer                                                                                                                                 | Empty-skip? |
| ----------------------------- | ------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------- | ----------- |
| `oauth:google:client_secret`  | `BootstrapConfig.OAuth.GoogleClientSecret`  | `SecretsBootstrapService` → `AuthenticationConfiguration.GoogleOAuthClientSecret`                                                        | yes         |
| `oauth:discord:client_secret` | `BootstrapConfig.OAuth.DiscordClientSecret` | `SecretsBootstrapService` → `AuthenticationConfiguration.DiscordOAuthClientSecret`                                                       | yes         |
| `oauth:apple:client_secret`   | `BootstrapConfig.OAuth.AppleClientSecret`   | `SecretsBootstrapService` → `AuthenticationConfiguration.AppleOAuthClientSecret`                                                         | yes         |
| `encryption:pepper`           | `GeneratedSecrets.EncryptionPepper`         | `SecretsBootstrapService` → `AuthenticationConfiguration.EncryptionPepper`                                                               | no          |
| `postgres:admin_username`     | constant `interfold_admin`                  | `PostgresMigrationService` (DDL connection)                                                                                              | no          |
| `postgres:admin_password`     | `GeneratedSecrets.PostgresAdminPassword`    | `PostgresMigrationService`                                                                                                               | no          |
| `scylla:admin_username`       | `GeneratedSecrets.ScyllaUser + "_admin"`    | `ScyllaMigrationService` (keyspace DDL)                                                                                                  | no          |
| `scylla:admin_password`       | `GeneratedSecrets.ScyllaAdminPassword`      | `ScyllaMigrationService`                                                                                                                 | no          |
| `scylla:contact_points`       | AppHost-resolved Scylla host                | `ScyllaSessionProvider` / health checker                                                                                                 | no          |
| `scylla:local_datacenter`     | `nam`                                       | `ScyllaSessionProvider`                                                                                                                  | no          |
| `scylla:username`             | `GeneratedSecrets.ScyllaUser`               | `ScyllaSessionProvider` (app session)                                                                                                    | no          |
| `scylla:password`             | `GeneratedSecrets.ScyllaPassword`           | `ScyllaSessionProvider`                                                                                                                  | no          |
| `scylla:port`                 | `Ports.scylla` (default `9042`)             | `ScyllaSessionProvider`                                                                                                                  | no          |
| `auth:jwt_rsa256_private_pem` | `GeneratedSecrets.JwtRsa256PrivateKeyPem`   | `SecretsBootstrapService.PatchRsa256` — populates `Rsa256PrivateKey` + derives `Rsa256PublicKey`                                         | yes         |
| `auth:jwt_es256_private_pem`  | `GeneratedSecrets.JwtEs256PrivateKeyPem`    | `SecretsBootstrapService.PatchEs256` — populates `JwtEs256PrivateKeyPem` + seeds `JwtEs256VerificationKeyPems[0]`                        | yes         |
| `auth:deep_link_secret`       | `GeneratedSecrets.DeepLinkSecret`           | `SecretsBootstrapService` → `AuthenticationConfiguration.DeepLinkSecret`                                                                 | yes         |
| `certs:leaf_pfx_password`     | `GeneratedSecrets.LeafPfxPassword`          | `SecretsPreBuildLoader` — injected into `IConfiguration[Kestrel:Certificates:Default:Password]` before host build | yes         |
| `firebase:client:android`     | `FirebasePhase` parses `google-services.json` | `SecretsBootstrapService` → `FirebaseClientConfiguration.Android` — served by `GET /api/settings/firebase-config?platform=android`      | yes         |
| `firebase:client:ios`         | `FirebasePhase` parses `GoogleService-Info.plist` | `SecretsBootstrapService` → `FirebaseClientConfiguration.Ios` — served by `GET /api/settings/firebase-config?platform=ios`          | yes         |
| `firebase:client:web`         | `FirebasePhase` reads `firebase-web-config.json` | `SecretsBootstrapService` → `FirebaseClientConfiguration.Web` — served by `GET /api/settings/firebase-config?platform=web`         | yes         |
| `fcm:service_account_json`    | `FirebasePhase` reads verbatim from operator-supplied file | `IFCMService` DI factory in `ClusterServiceCollectionExtensions` — absent row → `NullFCMService` fallback, present → `FirebaseFCMService` | yes         |


> **Deliberately absent:** there is no `scylla:keyspace` row. Keyspace is per-node region
> identity and must come from the deployment env (`OCTOCON_SCYLLA_KEYSPACE`), not from a
> shared cluster row.

The OAuth client-IDs do **not** appear in this table by design. They are public values; the
asymmetric split (IDs in env, secrets in store) is intentional and the seed list reflects it.

### Firebase / FCM

Push notifications are optional — a deployment can ship without any of the four Firebase
rows seeded and the whole flow degrades gracefully:

- The three `firebase:client:*` rows carry the **public** per-platform init payloads that
  the mobile / wasm clients fetch at runtime via `GET /api/settings/firebase-config?platform=…`
  instead of baking the values into each build. They are safe to hand out anonymously —
  they authorise nothing on their own. An absent row makes the endpoint return `503
  firebase_config_unavailable` for that platform; the client's `FirebaseConfigProvider`
  falls back to its offline behaviour (no push, but the rest of the app still works).
- `fcm:service_account_json` carries the **private** FCM v1 service-account credential
  the API uses to *send* notifications. This is a full Google Cloud IAM credential and
  must be protected as such. When absent, the `IFCMService` DI factory selects
  `NullFCMService` so `FrontNotifierBackgroundService` no-ops the send path — no
  fronting-change push, no crash, no operator action required.

Operators supply the source files via `BootstrapConfig.firebase.*`:

| Field                                | File the operator points at                                         | How to obtain it                                                                                                       |
| ------------------------------------ | ------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------- |
| `firebase.androidConfigPath`         | `google-services.json`                                              | Firebase Console → Project Settings → General → Your apps → Android → *google-services.json*                           |
| `firebase.iosConfigPath`             | `GoogleService-Info.plist`                                          | Firebase Console → Project Settings → General → Your apps → iOS → *GoogleService-Info.plist*                           |
| `firebase.webConfigPath`             | flat `firebase-web-config.json` (matches the client DTO 1:1)         | Firebase Console → Project Settings → General → Your apps → Web → *SDK setup and configuration* + inject the VAPID key from Cloud Messaging → Web configuration → Web Push certificates |
| `firebase.serviceAccountPath`        | FCM v1 service-account credential JSON                              | Firebase Console → Project Settings → Service accounts → *Generate new private key*                                    |

The `firebase-phase` in the bootstrapper (`Interfold.Bootstrapper/Phases/FirebasePhase.cs`)
ingests each configured file, reshapes the platform-specific inputs into the snake-case
JSON the API deserialises, and threads the four resulting seed strings into
`PostgresSeedOptions.FirebaseAndroidClientJson`/`FirebaseIosClientJson`/
`FirebaseWebClientJson`/`FcmServiceAccountJson`. `PostgresSeeder.BootstrapAsync` then
writes them as `internal.secrets` rows in the same idempotent pass as the OAuth secrets.

Every path is optional — leaving one blank is the supported "skip this input" shape.
A non-empty path that doesn't resolve to a file OR that fails to parse is a hard
bootstrap failure, so a typo surfaces at bootstrap time rather than at first API
request. Store the source files with the same 0600 filesystem mode as
`secrets/secrets.json` — the FCM service-account credential in particular is a
long-lived Google Cloud IAM key.

## Boot ordering

The order of operations on a self-hosted API container start is:

```mermaid
sequenceDiagram
    autonumber
    participant Operator
    participant Compose as Docker Compose
    participant API as API Program.cs
    participant PG as Postgres (internal.secrets)
    participant Host as ASP.NET Core host
    participant Kestrel
    participant SBS as SecretsBootstrapService<br/>(IHostedLifecycleService)
    participant Mig as Migration services

    Operator->>Compose: docker compose up -d
    Compose->>API: start container, exec dotnet Interfold.Api
    API->>API: build IConfiguration<br/>(env + appsettings)
    API->>PG: SELECT value WHERE key='certs:leaf_pfx_password'
    PG-->>API: leaf PFX password
    API->>API: inject into Kestrel:Certificates:Default:Password
    API->>Host: builder.Build()
    Host->>Kestrel: bind HTTPS endpoint (loads leaf.pfx with the password)
    Host->>SBS: StartingAsync
    SBS->>PG: load auth:* + oauth:* + encryption:pepper rows
    SBS->>SBS: patch IOptionsMonitor<AuthenticationConfiguration>
    Host->>Mig: StartingAsync (Postgres + Scylla migrations)
    Mig->>PG: read admin credentials, run DDL
    Host->>API: StartedAsync; serve traffic
```



Two ordering invariants are critical:

1. **Kestrel ↔ leaf PFX password.** Kestrel reads
  `Kestrel:Certificates:Default:Password` out of `IConfiguration` *during* `builder.Build()`
   (specifically, when it binds the HTTPS endpoint). `IHostedLifecycleService.StartingAsync`
   runs *after* the host is built, which is too late. Hence the dedicated `NpgsqlConnection`
   query in `Program.cs` *before* `builder.Build()`. The failure mode if Postgres is
   unreachable here is "the API fails to start before binding" — louder than a missing
   cert at request time, and the deliberate trade-off documented in the source comment.
2. `**SecretsBootstrapService` ↔ migration services.** Migration services need the admin
  credentials from `internal.secrets` (`postgres:admin_password`, `scylla:admin_`*). They
   read those directly via `ISecretsStore`, so they don't actually depend on
   `SecretsBootstrapService`. But the auth options consumed by controllers (JWT private
   keys, deep-link secret) *do* depend on `SecretsBootstrapService` having patched them
   first. .NET's `IHostedLifecycleService.StartingAsync` runs all registered services
   concurrently, but the API doesn't accept requests until `StartedAsync` returns, so any
   controller that reads `IOptionsMonitor<AuthenticationConfiguration>` already sees
   patched values.

## Migration ledger

Both database providers track which embedded migrations have already been applied so
subsequent startups skip already-applied files instead of relying purely on `IF NOT EXISTS`
guards in the SQL/CQL. The ledger also detects post-deploy edits to applied files
(SHA-256 checksum drift) and refuses to start the API until the drift is resolved.

### Where the ledger lives

| Provider | Table                                                            | Scope key              | Created by                                            |
| -------- | ---------------------------------------------------------------- | ---------------------- | ----------------------------------------------------- |
| Postgres | `internal.schema_migrations` (`version` primary key)             | filename only          | `PostgresMigrationService.EnsureLedgerAsync`          |
| Scylla   | `global.schema_migrations` (`PRIMARY KEY (scope, version)`)      | `(keyspace, filename)` | `ScyllaMigrationService.EnsureLedgerAsync`            |

Each row records `checksum` (hex SHA-256 of the embedded resource bytes), `applied_at`,
`duration_ms`, and `applied_by` (the assembly informational version of the runner that
wrote the row). For Scylla the `scope` column is the regional keyspace name for the per-
region templates (`001_create_interfold_keyspaces.cql`,
`002_create_interfold_schema.templated.cql`) and `grants:<keyspace>` for the GRANT loop
(version = `grants_v1`, bump that constant in
`[ScyllaMigrationService.cs](../infrastructure/Interfold.Infrastructure.Scylla/ScyllaMigrationService.cs)`
when the grant set changes).

### Bootstrap order

- **Postgres:** the runner acquires the session-level
  `pg_advisory_lock(MigrationAdvisoryLockId)` first, then `CREATE SCHEMA IF NOT EXISTS
  internal` + `CREATE TABLE IF NOT EXISTS internal.schema_migrations` *before* querying
  the ledger. Each not-yet-applied migration body and its ledger insert run inside the
  same `NpgsqlTransaction` so a partial failure leaves no orphan row.
- **Scylla:** the runner renders `000_create_singleton_keyspaces.cql` unconditionally to
  create `global` / `nam_nt` / `dummy` (the only "always-run" bootstrap step, untracked
  because `global` is the precondition for the ledger itself), then creates
  `global.schema_migrations` and reads it. Per-keyspace files (`001_*`,
  `002_*.templated.cql`) are then rendered and applied per regional keyspace, with each
  ledger insert issued as `INSERT IF NOT EXISTS` so concurrent migrators converge on a
  single row.

### Drift behaviour

If a recorded migration's recomputed checksum no longer matches the row, the runner
throws `InvalidOperationException` mentioning the version and both checksums and the API
refuses to start. Migration files are immutable once applied. To intentionally repurpose
an existing version (e.g., you re-wrote the file and want to mark it re-applied), pick the
appropriate path:

- **Force re-record (no DB change required):** update the ledger checksum to the new file
  hash before restart. The runner will see the row exists with the new checksum and skip
  the file body next start. Compute the new checksum locally with `sha256sum` and:
  - Postgres:
    `UPDATE internal.schema_migrations SET checksum = '<NEW_HEX>' WHERE version = '<file>';`
  - Scylla:
    `UPDATE global.schema_migrations SET checksum = '<NEW_HEX>' WHERE scope = '<keyspace>' AND version = '<file>';`
- **Force re-run from scratch (rare):** delete the ledger row(s) for the file. The runner
  will treat the file as new on the next start, run the body, and re-insert the row. Only
  do this if every statement in the file remains idempotent (`CREATE … IF NOT EXISTS`,
  etc.) — the runner does not roll back the schema before re-applying.

Both rewrites require admin credentials (the app user has no access to `internal.secrets`-
adjacent tables or `global.schema_migrations`); use the same `postgres:admin_*` /
`scylla:admin_*` rows the runner consumes.

## Rotation story

Three independent rotation surfaces, each with a single command:


| Rotation          | Command                                             | What changes                                                                         | What stays                                                            |
| ----------------- | --------------------------------------------------- | ------------------------------------------------------------------------------------ | --------------------------------------------------------------------- |
| **Secrets**       | `interfold-bootstrap rotate-secrets`                | All DB passwords, encryption pepper, JWT RSA + ES256 keypairs, deep-link HMAC secret | Certs (root CA + leaf), leaf PFX password (preserved across rotation) |
| **Certs**         | `interfold-bootstrap rotate-certs`                  | Root CA + leaf cert/key, leaf PFX wrapper (re-encrypted with the existing password)  | All secrets                                                           |
| **OAuth secrets** | edit `interfold.bootstrap.json` → rerun `bootstrap` | Only the OAuth secrets you changed                                                   | Everything else                                                       |


Mechanics:

- `rotate-secrets` deliberately preserves `LeafPfxPassword` (see `SecretsPhase.RunAsync`)
because the wrapped PFX bytes don't change — re-wrapping with a fresh password would
invalidate Kestrel's load with no security benefit.
- Backfill on idempotent reruns: if a `secrets.json` file from before the JWT-keys-in-store
migration is re-read without `--rotate-secrets`, `SecretsPhase` backfills any missing
fields (`DeepLinkSecret`, `JwtRsa256PrivateKeyPem`, `JwtEs256PrivateKeyPem`,
`PostgresAdminPassword`, `ScyllaAdminPassword`) so the next bootstrap pass writes them
into `internal.secrets`.
- OAuth client *secrets* can be rotated without touching the bootstrapper by issuing
`UPDATE internal.secrets SET value = '...' WHERE key = 'oauth:<provider>:client_secret';`
followed by an API restart. The bootstrapper will catch up on the next run.

## Local dev vs self-host vs tests


| Concern           | Local dev (`dotnet run --project hosts/Interfold.AppHost`)                                                                                                  | Self-host (`interfold-bootstrap`)                                             | Integration tests                                                                                                                         |
| ----------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------- |
| Encryption pepper | `Parameters:dev-encryption-pepper` (`GenerateParameterDefault`, persisted to AppHost user-secrets) → `internal.secrets:encryption:pepper` via dev-seed hook | `GeneratedSecrets.EncryptionPepper` → `internal.secrets:encryption:pepper`    | Postgres fixtures seed `"TEST"` into the row via `PostgresSeedOptions`; the in-memory store is pre-seeded with `"TEST"` for inmemory mode |
| JWT keys          | Generated per-run in `DevSeedHostedService` (RSA-2048 + P-256), written into `internal.secrets` on the first seed — reruns hit the seeder's idempotency short-circuit and reuse the row | Generated lazily by `SecretsPhase` and round-tripped through Postgres         | Seeded into the real store for Scylla/Postgres fixtures; in-memory store pre-seeded with `TestDbCredentials` PEMs for inmemory mode       |
| Deep-link secret  | `Parameters:dev-deep-link-secret` (`GenerateParameterDefault`, persisted to AppHost user-secrets) → `internal.secrets:auth:deep_link_secret` via dev-seed hook | `GeneratedSecrets.DeepLinkSecret` → `internal.secrets:auth:deep_link_secret`  | `TestDbCredentials.DeepLinkSecret` via `PostgresSeedOptions`                                                                              |
| Admin passwords   | `Parameters:dev-postgres-admin-password` / `Parameters:dev-scylla-admin-password` (both `GenerateParameterDefault`, persisted to AppHost user-secrets)     | `GeneratedSecrets.PostgresAdminPassword` / `ScyllaAdminPassword`              | `TestDbCredentials.Postgres/Scylla AdminPassword`                                                                                         |
| Leaf PFX password | *no leaf PFX in dev* (ASP.NET dev cert)                                                                                                                     | `internal.secrets:certs:leaf_pfx_password`, loaded by `SecretsPreBuildLoader` | not exercised                                                                                                                             |
| OAuth secrets     | Empty in the dev-seed row (`OAuthChallenge` extensions disable the provider for that row)                                                                   | `internal.secrets:oauth:*:client_secret`                                      | empty / `"TEST"`                                                                                                                          |

The `Parameters:dev-*` names above are AppHost-private (declared in
[`DevSeedParameterNames`](../hosts/Interfold.AppHost/DevSeed/DevSeedParameterNames.cs)) —
they never round-trip through the bootstrapper's `PublishPhase.BuildEnvReplacements` so
they can't leak into the emitted `.env`. To rotate any of them, run
`dotnet user-secrets clear --project hosts/Interfold.AppHost` and wipe the persistent
Postgres volume; a fresh AppHost run will re-generate and re-seed. Wiping user-secrets
without wiping the DB leaves stale-encrypted data behind — the same trade-off
`rotate-secrets` documents for prod (see `docs/ROADMAP.md`).


Tests centralise the test-only material in
`[TestDbCredentials](../tests/integration/Interfold.IntegrationTests.Shared/TestServices/TestDbCredentials.cs)`
— a single source of lazy-generated in-process keypairs and deterministic passwords. The
real DB fixtures seed those values into `internal.secrets` via `PostgresSeedOptions`; the
in-memory `WebApplicationFactory` instead drives the production env-var seed path by
pushing the same PEMs + pepper into the factory's configuration provider (see the
constructor of
`[InterfoldWebApplicationFactory](../tests/integration/Interfold.IntegrationTests.Shared/TestServices/InterfoldWebApplicationFactory.cs)`).
External runners (e.g. the Kotlin Testcontainers harness) set them as
`OCTOCON_INMEMORY_SECRETS_SEED__*` env vars on the container; the .NET
`EnvironmentVariablesConfigurationProvider` rewrites the `__` separator to the config-key
delimiter `:` on load, so the in-memory `ISecretsStore` registration in
`[InMemoryServiceCollectionExtensions](../infrastructure/Interfold.Infrastructure.InMemory/InMemoryServiceCollectionExtensions.cs)`
looks them up via `IConfiguration` under the `:`-form key
(`OCTOCON_INMEMORY_SECRETS_SEED:ENCRYPTION_PEPPER`, …) and seeds the store. The
in-process test fixture writes the same `:`-form keys into its
`FactoryConfigurationProvider` so both code paths land on the identical lookup, and a
dedicated regression test
(`Api_InMemorySecretsSeed_PatchesAuthFromRealEnvVars`) additionally mutates the
operator-facing `__`-form env vars via `Environment.SetEnvironmentVariable` to lock the
real env-var ingestion path end-to-end. Either way, signing in `CreateToken` and
verification on the server side use the same PEMs.

## Common gotchas

- `**OCTOCON_SCYLLA_KEYSPACE` is required for correct routing.** It used to fall back to
`internal.secrets:scylla:keyspace`; that row no longer exists. The bootstrapper now
manages this env var end-to-end (sourced from `BootstrapConfig.scyllaKeyspace`,
defaulted to `nam`, validated against the seven regional values), so a bootstrap-emitted
stack always ships with an explicit value. The gotcha survives for operators running the
API container *outside* the bootstrapper-produced compose stack: if the env is missing in
that path, the API defaults to `"nam"` in code — correct for a single-region deployment
but the *wrong* answer for a `eur` or `gdpr` node. Failure mode is "wrong region", not
"crash".
- **Don't put `ASPNETCORE_Kestrel__Certificates__Default__Password` back in `.env`.** It
is intentionally not generated. If you set it, `SecretsPreBuildLoader`
honours it as an override (legacy escape hatch), but you've now bypassed
`internal.secrets` and rotation via `rotate-secrets` will not propagate.
- `**internal.secrets:encryption:pepper` must exist before the API starts.** It's the
one row `SecretsBootstrapService` enforces — if the value is missing or empty the API
refuses to boot rather than failing on the first encryption request with an opaque
`NullReferenceException`. `DatabaseInitPhase` seeds it from `GeneratedSecrets.EncryptionPepper`;
tests pass `"TEST"` through `PostgresSeedOptions` or `InMemorySecretsStore.Seed`. The
pepper no longer has an env-var fallback — the previous `OCTOCON_ENCRYPTION_PEPPER` is dead.
- **OAuth client IDs in env, secrets in store.** Client IDs in env are public and that's
fine. Putting client *secrets* in env (other than the bootstrapper-written placeholders)
is a foot-gun: the env value is overwritten at startup, so an operator who manually
edits `.env` will be confused when their change has no effect.
- **The `/keys` bind mount is gone.** Earlier versions mounted `secrets/keys/*.pem` into
`/keys` inside the container. Those PEMs are no longer written to disk; the API reads
the same material from `internal.secrets`. Remove any old `/keys` mounts from custom
compose overrides.

