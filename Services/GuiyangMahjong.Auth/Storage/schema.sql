CREATE TABLE IF NOT EXISTS auth_identities (
    installation_hash CHAR(64) PRIMARY KEY,
    player_id VARCHAR(80) NOT NULL UNIQUE,
    display_name VARCHAR(24) NOT NULL,
    provider VARCHAR(32) NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL,
    updated_at_utc TIMESTAMPTZ NOT NULL
);

CREATE TABLE IF NOT EXISTS auth_refresh_sessions (
    session_id CHAR(32) PRIMARY KEY,
    player_id VARCHAR(80) NOT NULL REFERENCES auth_identities(player_id) ON DELETE CASCADE,
    token_hash BYTEA NOT NULL,
    expires_at_utc TIMESTAMPTZ NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL,
    revoked_at_utc TIMESTAMPTZ NULL
);

CREATE INDEX IF NOT EXISTS ix_auth_refresh_player_expiry
    ON auth_refresh_sessions(player_id, expires_at_utc DESC);

CREATE TABLE IF NOT EXISTS auth_login_events (
    event_id UUID PRIMARY KEY,
    player_id VARCHAR(80) NOT NULL REFERENCES auth_identities(player_id) ON DELETE CASCADE,
    device_id VARCHAR(40) NOT NULL,
    masked_ip VARCHAR(64) NOT NULL,
    client_summary VARCHAR(160) NOT NULL,
    outcome VARCHAR(24) NOT NULL,
    occurred_at_utc TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_auth_login_player_time
    ON auth_login_events(player_id, occurred_at_utc DESC);

CREATE INDEX IF NOT EXISTS ix_auth_login_device_time
    ON auth_login_events(device_id, occurred_at_utc DESC);

CREATE TABLE IF NOT EXISTS auth_admin_commands (
    command_id VARCHAR(128) PRIMARY KEY,
    command_type VARCHAR(64) NOT NULL,
    target_id VARCHAR(80) NOT NULL,
    effective_at_utc TIMESTAMPTZ NOT NULL,
    processed_at_utc TIMESTAMPTZ NOT NULL,
    player_found BOOLEAN NOT NULL,
    affected_count INTEGER NOT NULL
);
