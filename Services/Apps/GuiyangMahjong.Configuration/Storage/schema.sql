-- Configuration Service 独占 configuration 与 configuration_integration Schema。
-- 生产环境只能由独立 migration 身份执行本文件，运行身份仅授予所需 DML 权限。
CREATE SCHEMA IF NOT EXISTS configuration;
CREATE SCHEMA IF NOT EXISTS configuration_integration;

-- 草稿保存待审正本及完整审批链；revision 用于乐观并发，避免审批与发布互相覆盖。
CREATE TABLE IF NOT EXISTS configuration.config_drafts (
    draft_id UUID PRIMARY KEY,
    config_key VARCHAR(120) NOT NULL,
    schema_version INTEGER NOT NULL CHECK (schema_version > 0),
    payload JSONB NOT NULL,
    payload_hash CHAR(64) NOT NULL,
    status VARCHAR(20) NOT NULL CHECK (status IN ('Draft','Validated','Approved','Published','Rejected')),
    created_by VARCHAR(128) NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL,
    validated_by VARCHAR(128),
    validated_at_utc TIMESTAMPTZ,
    approved_by VARCHAR(128),
    approved_at_utc TIMESTAMPTZ,
    reason_code VARCHAR(80),
    ticket_id VARCHAR(128) NOT NULL,
    trace_id VARCHAR(64) NOT NULL,
    idempotency_key VARCHAR(128) NOT NULL,
    revision BIGINT NOT NULL CHECK (revision > 0),
    CONSTRAINT ux_config_draft_idempotency UNIQUE (created_by, idempotency_key)
);
CREATE INDEX IF NOT EXISTS ix_config_drafts_status_created
    ON configuration.config_drafts(status, created_at_utc DESC);

-- 发布版本只能追加。payload_hash 与 signature 使服务可在原子切换前独立验真。
CREATE TABLE IF NOT EXISTS configuration.config_versions (
    version_id UUID PRIMARY KEY,
    config_key VARCHAR(120) NOT NULL,
    version_number BIGINT NOT NULL CHECK (version_number > 0),
    schema_version INTEGER NOT NULL CHECK (schema_version > 0),
    payload JSONB NOT NULL,
    payload_hash CHAR(64) NOT NULL,
    signature CHAR(64) NOT NULL,
    published_at_utc TIMESTAMPTZ NOT NULL,
    published_by VARCHAR(128) NOT NULL,
    approved_by VARCHAR(128) NOT NULL,
    ticket_id VARCHAR(128) NOT NULL,
    trace_id VARCHAR(64) NOT NULL,
    rollback_of_version BIGINT,
    idempotency_key VARCHAR(128) NOT NULL,
    CONSTRAINT ux_config_version UNIQUE (config_key, version_number),
    CONSTRAINT ux_config_publish_idempotency UNIQUE (idempotency_key)
);

-- 当前指针是唯一可变元数据；版本正文本身不更新，回滚也创建更高版本。
CREATE TABLE IF NOT EXISTS configuration.config_current (
    config_key VARCHAR(120) PRIMARY KEY,
    version_id UUID NOT NULL REFERENCES configuration.config_versions(version_id),
    version_number BIGINT NOT NULL,
    updated_at_utc TIMESTAMPTZ NOT NULL
);

-- 应用回执只记录低基数部署维度和结果，不保存配置正文、玩家或房间标识。
CREATE TABLE IF NOT EXISTS configuration.config_application_reports (
    report_id UUID PRIMARY KEY,
    config_key VARCHAR(120) NOT NULL,
    version_number BIGINT NOT NULL,
    service_name VARCHAR(128) NOT NULL,
    service_version VARCHAR(80) NOT NULL,
    region VARCHAR(80) NOT NULL,
    cell VARCHAR(80) NOT NULL,
    result VARCHAR(32) NOT NULL CHECK (result IN ('Applied','Rejected','UsingLastKnownGood')),
    error_code VARCHAR(120),
    applied_at_utc TIMESTAMPTZ NOT NULL,
    trace_id VARCHAR(64) NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_config_reports_version
    ON configuration.config_application_reports(config_key, version_number, applied_at_utc DESC);

-- 版本切换与发布事件同事务提交；NATS 中断时 Worker 可稍后继续发送。
CREATE TABLE IF NOT EXISTS configuration_integration.platform_outbox (
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
    lock_owner TEXT,
    lease_expires_at TIMESTAMPTZ,
    published_at TIMESTAMPTZ,
    error_summary TEXT
);
CREATE INDEX IF NOT EXISTS ix_configuration_outbox_dispatch
    ON configuration_integration.platform_outbox(status, next_attempt_at, lease_expires_at);
CREATE TABLE IF NOT EXISTS configuration_integration.platform_outbox_archive
    (LIKE configuration_integration.platform_outbox INCLUDING ALL);

-- 防止运行账号或误操作原地覆盖已发布版本；历史修正只能发布补偿版本。
CREATE OR REPLACE FUNCTION configuration.reject_version_mutation()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'published configuration versions are immutable';
END;
$$;
DROP TRIGGER IF EXISTS trg_config_versions_immutable ON configuration.config_versions;
CREATE TRIGGER trg_config_versions_immutable
BEFORE UPDATE OR DELETE OR TRUNCATE ON configuration.config_versions
FOR EACH STATEMENT EXECUTE FUNCTION configuration.reject_version_mutation();
