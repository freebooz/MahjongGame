-- 阶段 2 基础构件迁移模板。
-- 部署工具必须把 __SCHEMA__ 替换为消费服务自有、经过 ^[a-z][a-z0-9_]{0,62}$ 校验的 Schema。
-- 不得把多个服务长期配置到同一个 Schema，也不得由普通生产运行账号执行本文件。

CREATE SCHEMA IF NOT EXISTS "__SCHEMA__";

CREATE TABLE IF NOT EXISTS "__SCHEMA__".platform_outbox (
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
CREATE INDEX IF NOT EXISTS ix_platform_outbox_dispatch
    ON "__SCHEMA__".platform_outbox(status, next_attempt_at, lease_expires_at);

CREATE TABLE IF NOT EXISTS "__SCHEMA__".platform_outbox_archive
    (LIKE "__SCHEMA__".platform_outbox INCLUDING ALL);

CREATE TABLE IF NOT EXISTS "__SCHEMA__".platform_inbox (
    consumer_name TEXT NOT NULL,
    event_id TEXT NOT NULL,
    event_type TEXT NOT NULL,
    schema_version INTEGER NOT NULL CHECK (schema_version > 0),
    status TEXT NOT NULL CHECK (status IN ('Processing','Completed','Failed')),
    received_at TIMESTAMPTZ NOT NULL,
    completed_at TIMESTAMPTZ NULL,
    failure_count INTEGER NOT NULL DEFAULT 0 CHECK (failure_count >= 0),
    error_summary TEXT NULL,
    PRIMARY KEY (consumer_name, event_id)
);
CREATE INDEX IF NOT EXISTS ix_platform_inbox_cleanup
    ON "__SCHEMA__".platform_inbox(status, completed_at);

CREATE TABLE IF NOT EXISTS "__SCHEMA__".platform_idempotency (
    scope TEXT NOT NULL,
    idempotency_key TEXT NOT NULL,
    request_fingerprint CHAR(64) NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('Processing','Completed','Failed')),
    created_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    response_status INTEGER NULL,
    response_content_type TEXT NULL,
    response_body BYTEA NULL,
    error_summary TEXT NULL,
    PRIMARY KEY (scope, idempotency_key)
);
CREATE INDEX IF NOT EXISTS ix_platform_idempotency_expiry
    ON "__SCHEMA__".platform_idempotency(expires_at);
