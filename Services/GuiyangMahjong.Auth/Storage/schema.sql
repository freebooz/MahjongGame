-- IdentityApp 的逻辑 Schema 由独立迁移身份创建；运行时账号仅获得所需 DML 权限。
CREATE SCHEMA IF NOT EXISTS auth;
CREATE SCHEMA IF NOT EXISTS session;
CREATE SCHEMA IF NOT EXISTS player;
CREATE SCHEMA IF NOT EXISTS integration;

-- 从阶段 3 之前的 public 表执行原位迁移。ALTER TABLE SET SCHEMA 保留数据、索引和外键，
-- 条件判断保证脚本可重复执行，也避免新旧表并存后出现双写。
DO $$
BEGIN
    IF to_regclass('public.auth_identities') IS NOT NULL
       AND to_regclass('auth.auth_identities') IS NULL THEN
        ALTER TABLE public.auth_identities SET SCHEMA auth;
    END IF;
    IF to_regclass('public.auth_refresh_sessions') IS NOT NULL
       AND to_regclass('session.auth_refresh_sessions') IS NULL THEN
        ALTER TABLE public.auth_refresh_sessions SET SCHEMA session;
    END IF;
    IF to_regclass('public.auth_login_events') IS NOT NULL
       AND to_regclass('integration.auth_login_events') IS NULL THEN
        ALTER TABLE public.auth_login_events SET SCHEMA integration;
    END IF;
    IF to_regclass('public.auth_admin_commands') IS NOT NULL
       AND to_regclass('auth.auth_admin_commands') IS NULL THEN
        ALTER TABLE public.auth_admin_commands SET SCHEMA auth;
    END IF;
    IF to_regclass('public.auth_player_controls') IS NOT NULL
       AND to_regclass('auth.auth_player_controls') IS NULL THEN
        ALTER TABLE public.auth_player_controls SET SCHEMA auth;
    END IF;
    IF to_regclass('public.auth_player_control_events') IS NOT NULL
       AND to_regclass('auth.auth_player_control_events') IS NULL THEN
        ALTER TABLE public.auth_player_control_events SET SCHEMA auth;
    END IF;
END
$$;

CREATE TABLE IF NOT EXISTS auth.auth_identities (
    installation_hash CHAR(64) PRIMARY KEY,
    player_id VARCHAR(80) NOT NULL UNIQUE,
    display_name VARCHAR(24) NOT NULL,
    provider VARCHAR(32) NOT NULL,
    session_epoch BIGINT NOT NULL DEFAULT 0,
    security_epoch BIGINT NOT NULL DEFAULT 0,
    created_at_utc TIMESTAMPTZ NOT NULL,
    updated_at_utc TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_auth_identity_session_epoch CHECK (session_epoch >= 0),
    CONSTRAINT ck_auth_identity_security_epoch CHECK (security_epoch >= 0)
);

ALTER TABLE auth.auth_identities
    ADD COLUMN IF NOT EXISTS session_epoch BIGINT NOT NULL DEFAULT 0;
ALTER TABLE auth.auth_identities
    ADD COLUMN IF NOT EXISTS security_epoch BIGINT NOT NULL DEFAULT 0;

CREATE INDEX IF NOT EXISTS ix_auth_identities_monitoring_cursor_v2
    ON auth.auth_identities(created_at_utc DESC, player_id DESC);

CREATE TABLE IF NOT EXISTS session.auth_refresh_sessions (
    session_id CHAR(32) PRIMARY KEY,
    player_id VARCHAR(80) NOT NULL
        REFERENCES auth.auth_identities(player_id) ON DELETE CASCADE,
    token_hash BYTEA NOT NULL,
    family_id CHAR(32) NOT NULL,
    parent_session_id CHAR(32) NULL,
    device_id VARCHAR(40) NOT NULL,
    session_epoch BIGINT NOT NULL DEFAULT 0,
    security_epoch BIGINT NOT NULL DEFAULT 0,
    expires_at_utc TIMESTAMPTZ NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL,
    revoked_at_utc TIMESTAMPTZ NULL,
    replaced_by_session_id CHAR(32) NULL,
    revocation_reason VARCHAR(64) NULL,
    reuse_detected_at_utc TIMESTAMPTZ NULL,
    CONSTRAINT ck_auth_refresh_session_epoch CHECK (session_epoch >= 0),
    CONSTRAINT ck_auth_refresh_security_epoch CHECK (security_epoch >= 0)
);

ALTER TABLE session.auth_refresh_sessions
    ADD COLUMN IF NOT EXISTS family_id CHAR(32);
ALTER TABLE session.auth_refresh_sessions
    ADD COLUMN IF NOT EXISTS parent_session_id CHAR(32);
ALTER TABLE session.auth_refresh_sessions
    ADD COLUMN IF NOT EXISTS device_id VARCHAR(40);
ALTER TABLE session.auth_refresh_sessions
    ADD COLUMN IF NOT EXISTS session_epoch BIGINT NOT NULL DEFAULT 0;
ALTER TABLE session.auth_refresh_sessions
    ADD COLUMN IF NOT EXISTS security_epoch BIGINT NOT NULL DEFAULT 0;
ALTER TABLE session.auth_refresh_sessions
    ADD COLUMN IF NOT EXISTS replaced_by_session_id CHAR(32);
ALTER TABLE session.auth_refresh_sessions
    ADD COLUMN IF NOT EXISTS revocation_reason VARCHAR(64);
ALTER TABLE session.auth_refresh_sessions
    ADD COLUMN IF NOT EXISTS reuse_detected_at_utc TIMESTAMPTZ;

-- 旧会话没有 Family 和设备档案；用自身 SessionId 建立单成员 Family，并使用不可逆的 Legacy 引用。
UPDATE session.auth_refresh_sessions
SET family_id = session_id
WHERE family_id IS NULL;
UPDATE session.auth_refresh_sessions
SET device_id = 'legacy-unknown'
WHERE device_id IS NULL;
ALTER TABLE session.auth_refresh_sessions
    ALTER COLUMN family_id SET NOT NULL;
ALTER TABLE session.auth_refresh_sessions
    ALTER COLUMN device_id SET NOT NULL;

CREATE INDEX IF NOT EXISTS ix_auth_refresh_player_expiry
    ON session.auth_refresh_sessions(player_id, expires_at_utc DESC);
CREATE INDEX IF NOT EXISTS ix_auth_refresh_family_active
    ON session.auth_refresh_sessions(player_id, family_id, revoked_at_utc);

CREATE TABLE IF NOT EXISTS player.player_profiles (
    player_id VARCHAR(80) PRIMARY KEY
        REFERENCES auth.auth_identities(player_id) ON DELETE CASCADE,
    display_name VARCHAR(24) NOT NULL,
    avatar_url VARCHAR(512) NULL,
    region VARCHAR(64) NULL,
    level INTEGER NOT NULL DEFAULT 1,
    settings_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    privacy_settings_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    updated_at_utc TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_player_profile_level CHECK (level >= 1)
);

-- 迁移既有昵称，使 Player 模块成为长期档案的明确所有者；auth.display_name 暂时保留为兼容快照。
INSERT INTO player.player_profiles(
    player_id, display_name, level, settings_json, privacy_settings_json, updated_at_utc)
SELECT player_id, display_name, 1, '{}'::jsonb, '{}'::jsonb, updated_at_utc
FROM auth.auth_identities
ON CONFLICT (player_id) DO NOTHING;

CREATE TABLE IF NOT EXISTS integration.auth_login_events (
    event_id UUID PRIMARY KEY,
    player_id VARCHAR(80) NOT NULL
        REFERENCES auth.auth_identities(player_id) ON DELETE CASCADE,
    device_id VARCHAR(40) NOT NULL,
    masked_ip VARCHAR(64) NOT NULL,
    client_summary VARCHAR(160) NOT NULL,
    outcome VARCHAR(24) NOT NULL,
    occurred_at_utc TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_auth_login_player_time
    ON integration.auth_login_events(player_id, occurred_at_utc DESC);
CREATE INDEX IF NOT EXISTS ix_auth_login_device_time
    ON integration.auth_login_events(device_id, occurred_at_utc DESC);

CREATE TABLE IF NOT EXISTS integration.auth_devices (
    player_id VARCHAR(80) NOT NULL
        REFERENCES auth.auth_identities(player_id) ON DELETE CASCADE,
    device_id VARCHAR(40) NOT NULL,
    trust_state VARCHAR(24) NOT NULL DEFAULT 'Unknown',
    risk_label_references TEXT[] NOT NULL DEFAULT '{}',
    first_seen_at_utc TIMESTAMPTZ NOT NULL,
    last_used_at_utc TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (player_id, device_id)
);

CREATE TABLE IF NOT EXISTS integration.auth_device_switch_events (
    event_id UUID PRIMARY KEY,
    player_id VARCHAR(80) NOT NULL
        REFERENCES auth.auth_identities(player_id) ON DELETE CASCADE,
    previous_device_id VARCHAR(40) NULL,
    current_device_id VARCHAR(40) NOT NULL,
    occurred_at_utc TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_auth_device_switch_player_time
    ON integration.auth_device_switch_events(player_id, occurred_at_utc DESC);

CREATE TABLE IF NOT EXISTS auth.auth_admin_commands (
    command_id VARCHAR(128) PRIMARY KEY,
    command_type VARCHAR(64) NOT NULL,
    target_id VARCHAR(80) NOT NULL,
    effective_at_utc TIMESTAMPTZ NOT NULL,
    processed_at_utc TIMESTAMPTZ NOT NULL,
    player_found BOOLEAN NOT NULL,
    affected_count INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS auth.auth_player_controls (
    player_id VARCHAR(80) PRIMARY KEY
        REFERENCES auth.auth_identities(player_id) ON DELETE CASCADE,
    version BIGINT NOT NULL,
    account_status VARCHAR(24) NOT NULL,
    frozen_until_utc TIMESTAMPTZ NULL,
    muted_until_utc TIMESTAMPTZ NULL,
    risk_labels TEXT[] NOT NULL DEFAULT '{}',
    risk_labels_expire_at_utc TIMESTAMPTZ NULL,
    updated_at_utc TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_auth_player_control_version CHECK (version >= 1),
    CONSTRAINT ck_auth_player_account_status
        CHECK (account_status IN ('Active', 'Frozen', 'Banned')),
    CONSTRAINT ck_auth_player_frozen_expiry
        CHECK (account_status <> 'Frozen' OR frozen_until_utc IS NOT NULL)
);

CREATE TABLE IF NOT EXISTS auth.auth_player_control_events (
    command_id VARCHAR(128) PRIMARY KEY,
    player_id VARCHAR(80) NOT NULL
        REFERENCES auth.auth_identities(player_id) ON DELETE CASCADE,
    action_type VARCHAR(64) NOT NULL,
    reason VARCHAR(500) NOT NULL,
    trace_id VARCHAR(64) NOT NULL,
    ticket_id VARCHAR(128) NOT NULL,
    requested_by VARCHAR(128) NOT NULL,
    approved_by VARCHAR(128) NOT NULL,
    effective_at_utc TIMESTAMPTZ NOT NULL,
    expires_at_utc TIMESTAMPTZ NULL,
    risk_label VARCHAR(64) NULL,
    expected_version BIGINT NOT NULL,
    revoked_session_count INTEGER NOT NULL,
    before_state JSONB NOT NULL,
    after_state JSONB NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_auth_player_control_events_player_time
    ON auth.auth_player_control_events(player_id, effective_at_utc DESC);

-- 会话撤销事实先写入 IdentityApp 本地 Outbox；本阶段不接入正式消息总线。
CREATE TABLE IF NOT EXISTS integration.identity_outbox (
    event_id UUID PRIMARY KEY,
    event_type VARCHAR(128) NOT NULL,
    schema_version INTEGER NOT NULL,
    aggregate_type VARCHAR(64) NOT NULL,
    aggregate_id VARCHAR(80) NOT NULL,
    occurred_at_utc TIMESTAMPTZ NOT NULL,
    trace_id VARCHAR(64) NULL,
    correlation_id VARCHAR(128) NULL,
    payload JSONB NOT NULL,
    published_at_utc TIMESTAMPTZ NULL,
    retry_count INTEGER NOT NULL DEFAULT 0,
    next_retry_at_utc TIMESTAMPTZ NULL,
    error_summary VARCHAR(512) NULL
);

CREATE INDEX IF NOT EXISTS ix_identity_outbox_pending
    ON integration.identity_outbox(next_retry_at_utc, occurred_at_utc)
    WHERE published_at_utc IS NULL;
