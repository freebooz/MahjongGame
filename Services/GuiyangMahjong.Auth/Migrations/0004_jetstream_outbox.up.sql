-- 阶段 9：增加统一平台 Outbox。生产由 Auth 迁移身份执行，运行身份只获得必要 DML。
CREATE SCHEMA IF NOT EXISTS identity_integration;
CREATE TABLE IF NOT EXISTS identity_integration.platform_outbox (
    event_id TEXT PRIMARY KEY,
    event_type TEXT NOT NULL,
    schema_version INTEGER NOT NULL CHECK (schema_version > 0),
    aggregate_type TEXT NOT NULL,
    aggregate_id TEXT NOT NULL,
    aggregate_version BIGINT NOT NULL CHECK (aggregate_version >= 0),
    payload_json JSONB NOT NULL,
    occurred_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('Pending','Processing','Published','Failed')),
    attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    next_attempt_at TIMESTAMPTZ NOT NULL,
    lock_owner TEXT NULL,
    lease_expires_at TIMESTAMPTZ NULL,
    published_at TIMESTAMPTZ NULL,
    error_summary TEXT NULL
);
CREATE INDEX IF NOT EXISTS ix_identity_platform_outbox_dispatch
    ON identity_integration.platform_outbox(status, next_attempt_at, lease_expires_at);
CREATE TABLE IF NOT EXISTS identity_integration.platform_outbox_archive
    (LIKE identity_integration.platform_outbox INCLUDING ALL);
