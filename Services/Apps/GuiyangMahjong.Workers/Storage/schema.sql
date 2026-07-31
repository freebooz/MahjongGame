CREATE SCHEMA IF NOT EXISTS worker_integration;
CREATE SCHEMA IF NOT EXISTS worker_projection;

-- 每个 Durable Consumer 使用 consumer_name + event_id 去重；业务投影和 Completed 状态同事务提交。
CREATE TABLE IF NOT EXISTS worker_integration.platform_inbox (
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
CREATE INDEX IF NOT EXISTS ix_worker_inbox_cleanup
    ON worker_integration.platform_inbox(status, completed_at);

-- 毒消息或超过最大投递次数的消息保留最小诊断信息，原始载荷不进入普通日志。
CREATE TABLE IF NOT EXISTS worker_integration.failed_events (
    failure_id UUID PRIMARY KEY,
    event_id TEXT NULL,
    subject TEXT NOT NULL,
    consumer_name TEXT NOT NULL,
    event_type TEXT NULL,
    schema_version INTEGER NULL,
    delivery_count BIGINT NOT NULL,
    error_code TEXT NOT NULL,
    error_summary TEXT NOT NULL,
    failed_at TIMESTAMPTZ NOT NULL,
    status TEXT NOT NULL DEFAULT 'PendingReview'
        CHECK (status IN ('PendingReview','RetryApproved','DiscardApproved','Resolved')),
    UNIQUE (consumer_name, subject, event_id)
);
CREATE INDEX IF NOT EXISTS ix_worker_failed_review
    ON worker_integration.failed_events(status, failed_at);

-- 每个消费者按聚合保存最高版本；迟到旧事件可以审计但不能覆盖新投影。
CREATE TABLE IF NOT EXISTS worker_integration.projection_checkpoints (
    consumer_name TEXT NOT NULL,
    aggregate_type TEXT NOT NULL,
    aggregate_id TEXT NOT NULL,
    aggregate_version BIGINT NOT NULL,
    event_id TEXT NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (consumer_name, aggregate_type, aggregate_id)
);

CREATE TABLE IF NOT EXISTS worker_projection.game_records (
    event_id TEXT PRIMARY KEY,
    event_type TEXT NOT NULL,
    aggregate_id TEXT NOT NULL,
    aggregate_version BIGINT NOT NULL,
    occurred_at TIMESTAMPTZ NOT NULL,
    payload_json JSONB NOT NULL
);

CREATE TABLE IF NOT EXISTS worker_projection.leaderboard_updates (
    event_id TEXT PRIMARY KEY,
    match_id TEXT NOT NULL,
    occurred_at TIMESTAMPTZ NOT NULL,
    payload_json JSONB NOT NULL
);

CREATE TABLE IF NOT EXISTS worker_projection.audit_events (
    event_id TEXT PRIMARY KEY,
    subject TEXT NOT NULL,
    event_type TEXT NOT NULL,
    aggregate_type TEXT NOT NULL,
    aggregate_id TEXT NOT NULL,
    occurred_at TIMESTAMPTZ NOT NULL,
    trace_id TEXT NOT NULL,
    correlation_id TEXT NOT NULL,
    payload_json JSONB NOT NULL
);
