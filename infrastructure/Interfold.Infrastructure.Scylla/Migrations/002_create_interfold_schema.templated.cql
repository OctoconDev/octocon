-- =====================
-- Schema Template: Replace {{KEYSPACE}} with regional keyspace (nam, eur, ocn, eas, sam, sas, gdpr)
-- Applied by ScyllaMigrationService at startup.
-- =====================

-- =====================
-- UDTs
-- =====================
CREATE TYPE IF NOT EXISTS {{KEYSPACE}}.discord_server_settings (
    guild_id text,
    proxying_disabled boolean,
    autoproxy_mode smallint,
    latched_alter int
);

CREATE TYPE IF NOT EXISTS {{KEYSPACE}}.discord_settings (
    system_tag text,
    show_system_tag boolean,
    case_insensitive_proxies boolean,
    show_pronouns boolean,
    ids_as_proxies boolean,
    silent_proxying boolean,
    use_proxy_delay boolean,
    global_autoproxy_mode smallint,
    global_latched_alter int,
    server_settings list<frozen<{{KEYSPACE}}.discord_server_settings>>
);

CREATE TYPE IF NOT EXISTS {{KEYSPACE}}.field (
    id uuid,
    name text,
    type smallint,
    locked boolean,
    security_level smallint
);

CREATE TYPE IF NOT EXISTS {{KEYSPACE}}.alter_field (
    id uuid,
    value text
);

CREATE TYPE IF NOT EXISTS nam.server_settings_data (
    log_channel text,
    force_system_tags boolean,
    proxy_disabled_users list<text>
);

-- =====================
-- USERS TABLE
-- avatar_source: 0 = Local (self-hosted via IAvatarStorage), 1 = External (passthrough URL).
-- Null when avatar_url is null.
-- =====================
CREATE TABLE IF NOT EXISTS {{KEYSPACE}}.users (
    id text,
    email text,
    discord_id text,
    apple_id text,
    google_id text,
    username text,
    description text,
    avatar_url text,
    avatar_source smallint,
    lifetime_alter_count int,
    primary_front int,
    last_proxy_id smallint,
    discord_settings frozen<{{KEYSPACE}}.discord_settings>,
    fields list<frozen<{{KEYSPACE}}.field>>,
    salt text,
    encryption_initialized boolean,
    encryption_key_checksum text,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (id)
);

-- =====================
-- USER IDENTITY LOOKUP TABLES (denormalized, replaces secondary indexes)
-- =====================
CREATE TABLE IF NOT EXISTS {{KEYSPACE}}.users_by_discord_id (
    discord_id text PRIMARY KEY,
    user_id text
);

CREATE TABLE IF NOT EXISTS {{KEYSPACE}}.users_by_email (
    email text PRIMARY KEY,
    user_id text
);

CREATE TABLE IF NOT EXISTS {{KEYSPACE}}.users_by_username (
    username text PRIMARY KEY,
    user_id text
);

CREATE TABLE IF NOT EXISTS {{KEYSPACE}}.users_by_apple_id (
    apple_id text PRIMARY KEY,
    user_id text
);

CREATE TABLE IF NOT EXISTS {{KEYSPACE}}.users_by_google_id (
    google_id text PRIMARY KEY,
    user_id text
);

-- =====================
-- ALTERS TABLE
-- avatar_source: 0 = Local (self-hosted via IAvatarStorage), 1 = External (passthrough URL).
-- Null when avatar_url is null.
-- =====================
CREATE TABLE IF NOT EXISTS {{KEYSPACE}}.alters (
    user_id text,
    id smallint,
    alias text,
    name text,
    pronouns text,
    description text,
    avatar_url text,
    avatar_source smallint,
    security_level smallint,
    extra_images list<text>,
    color text,
    discord_proxies list<text>,
    proxy_name text,
    fields list<frozen<{{KEYSPACE}}.alter_field>>,
    untracked boolean,
    archived boolean,
    pinned boolean,
    last_fronted timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, id)
);

-- =====================
-- ALTERS_BY_ALIAS LOOKUP TABLE (denormalized, replaces secondary index)
-- =====================
CREATE TABLE IF NOT EXISTS {{KEYSPACE}}.alters_by_alias (
    user_id text,
    alias text,
    alter_id smallint,
    PRIMARY KEY (user_id, alias)
);

-- =====================
-- TAGS TABLE
-- =====================
CREATE TABLE IF NOT EXISTS {{KEYSPACE}}.tags (
    user_id text,
    id uuid,
    parent_tag_id uuid,
    name text,
    description text,
    color text,
    security_level smallint,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, id)
);

-- =====================
-- ALTER_TAGS TABLE
-- =====================
CREATE TABLE IF NOT EXISTS {{KEYSPACE}}.alter_tags (
    user_id text,
    tag_id uuid,
    alter_id smallint,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, tag_id, alter_id)
);

-- =====================
-- ALTER_TAGS_BY_ALTER LOOKUP TABLE (denormalized, replaces secondary index)
-- =====================
CREATE TABLE IF NOT EXISTS {{KEYSPACE}}.alter_tags_by_alter (
    user_id text,
    alter_id smallint,
    tag_id uuid,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY ((user_id, alter_id), tag_id)
);

-- =====================
-- POLLS TABLE
-- =====================
CREATE TABLE IF NOT EXISTS {{KEYSPACE}}.polls (
    user_id text,
    id uuid,
    title text,
    description text,
    type smallint,
    data text,
    time_end timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, id)
);

-- =====================
-- GLOBAL_JOURNALS TABLE
-- =====================
CREATE TABLE IF NOT EXISTS {{KEYSPACE}}.global_journals (
    user_id text,
    id uuid,
    title text,
    content text,
    color text,
    pinned boolean,
    locked boolean,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, id)
);

-- =====================
-- GLOBAL_JOURNAL_ALTERS TABLE
-- =====================
CREATE TABLE IF NOT EXISTS {{KEYSPACE}}.global_journal_alters (
    user_id text,
    global_journal_id uuid,
    alter_id smallint,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, global_journal_id, alter_id)
);

-- =====================
-- CURRENT_FRONTS TABLE
-- =====================
CREATE TABLE IF NOT EXISTS {{KEYSPACE}}.current_fronts (
    user_id text,
    alter_id smallint,
    id uuid,
    comment text,
    time_start timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, alter_id)
);

-- =====================
-- ALTER_JOURNALS TABLE
-- =====================
CREATE TABLE IF NOT EXISTS {{KEYSPACE}}.alter_journals (
    user_id text,
    id uuid,
    alter_id smallint,
    title text,
    content text,
    color text,
    pinned boolean,
    locked boolean,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, id, alter_id)
);

-- =====================
-- ALTER_JOURNALS_BY_ALTER LOOKUP TABLE (denormalized, replaces secondary index)
-- =====================
CREATE TABLE IF NOT EXISTS {{KEYSPACE}}.alter_journals_by_alter (
    user_id text,
    alter_id smallint,
    id uuid,
    title text,
    content text,
    color text,
    pinned boolean,
    locked boolean,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY ((user_id, alter_id), id)
);

-- =====================
-- FRONTS TABLE
-- =====================
CREATE TABLE IF NOT EXISTS {{KEYSPACE}}.fronts (
    id uuid,
    user_id text,
    alter_id smallint,
    comment text,
    time_start timestamp,
    time_end timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, id, time_start)
);

-- =====================
-- FRONTS_BY_ALTER LOOKUP TABLE (denormalized, replaces materialized view)
-- =====================
CREATE TABLE IF NOT EXISTS {{KEYSPACE}}.fronts_by_alter (
    user_id text,
    alter_id smallint,
    id uuid,
    comment text,
    time_start timestamp,
    time_end timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY ((user_id, alter_id), id, time_start)
);

-- =====================
-- FRONTS_BY_TIME LOOKUP TABLE (denormalized, replaces materialized view)
-- Populated only when a front is closed (time_end is set).
-- =====================
CREATE TABLE IF NOT EXISTS {{KEYSPACE}}.fronts_by_time (
    user_id text,
    time_start timestamp,
    time_end timestamp,
    id uuid,
    alter_id smallint,
    comment text,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, time_start, time_end, id)
);

-- =====================
-- FRONTS_BY_END_TIME LOOKUP TABLE (denormalized, replaces materialized view)
-- Populated only when a front is closed (time_end is set).
-- =====================
CREATE TABLE IF NOT EXISTS {{KEYSPACE}}.fronts_by_end_time (
    user_id text,
    time_end timestamp,
    time_start timestamp,
    id uuid,
    alter_id smallint,
    comment text,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, time_end, time_start, id)
);

-- =====================
-- SERVER_SETTINGS TABLE (Nam-specific)
-- =====================
CREATE TABLE IF NOT EXISTS nam.server_settings (
    guild_id text,
    data frozen<nam.server_settings_data>,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (guild_id)
);

-- =====================
-- CHANNEL_BLACKLISTS TABLE (Nam-specific)
-- =====================
CREATE TABLE IF NOT EXISTS nam.channel_blacklists (
    channel_id text,
    guild_id text,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (channel_id)
);

-- =====================
-- CHANNEL_BLACKLISTS_BY_GUILD_ID LOOKUP TABLE (denormalized, replaces secondary index)
-- =====================
CREATE TABLE IF NOT EXISTS nam.channel_blacklists_by_guild_id (
    guild_id text,
    channel_id text,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (guild_id, channel_id)
);

-- =====================
-- GLOBAL KEYSPACE TABLES
-- =====================
CREATE TABLE IF NOT EXISTS global.user_registry (
    user_id text,
    discord_id text,
    email text,
    username text,
    apple_id text,
    google_id text,
    region text,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id)
);

-- =====================
-- GLOBAL USER_REGISTRY LOOKUP TABLES (denormalized, replaces secondary indexes)
-- =====================
CREATE TABLE IF NOT EXISTS global.user_registry_by_discord_id (
    discord_id text PRIMARY KEY,
    user_id text,
    region text
);

CREATE TABLE IF NOT EXISTS global.user_registry_by_email (
    email text PRIMARY KEY,
    user_id text,
    region text
);

CREATE TABLE IF NOT EXISTS global.user_registry_by_username (
    username text PRIMARY KEY,
    user_id text,
    region text
);

CREATE TABLE IF NOT EXISTS global.user_registry_by_apple_id (
    apple_id text PRIMARY KEY,
    user_id text,
    region text
);

CREATE TABLE IF NOT EXISTS global.user_registry_by_google_id (
    google_id text PRIMARY KEY,
    user_id text,
    region text
);

CREATE TABLE IF NOT EXISTS global.notification_tokens (
    user_id text,
    push_token text,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, push_token)
);

-- =====================
-- NOTIFICATION_TOKENS_BY_PUSH_TOKEN LOOKUP TABLE (denormalized, replaces secondary index)
-- =====================
CREATE TABLE IF NOT EXISTS global.notification_tokens_by_push_token (
    push_token text,
    user_id text,
    PRIMARY KEY (push_token, user_id)
);

CREATE TABLE IF NOT EXISTS global.friendships (
    user_id text,
    friend_id text,
    level smallint,
    since timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, friend_id)
);

-- =====================
-- FRIENDSHIPS_BY_FRIEND_ID LOOKUP TABLE (denormalized, replaces secondary index)
-- =====================
CREATE TABLE IF NOT EXISTS global.friendships_by_friend_id (
    friend_id text,
    user_id text,
    level smallint,
    since timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (friend_id, user_id)
);

CREATE TABLE IF NOT EXISTS global.friend_requests (
    from_id text,
    to_id text,
    date_sent timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (from_id, to_id)
);

-- =====================
-- FRIEND_REQUESTS_BY_TO_ID LOOKUP TABLE (denormalized, replaces secondary index)
-- =====================
CREATE TABLE IF NOT EXISTS global.friend_requests_by_to_id (
    to_id text,
    from_id text,
    date_sent timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (to_id, from_id)
);

-- =====================
-- METRICS KEYSPACE (nam_nt)
-- =====================
CREATE TABLE IF NOT EXISTS nam_nt.metrics_counts (
    key text,
    value counter,
    PRIMARY KEY (key)
);
