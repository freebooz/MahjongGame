-- GameData 独占五个逻辑 Schema。生产只允许 mahjong_migration 执行本文件；运行账号没有 DDL 权限。
CREATE SCHEMA IF NOT EXISTS settlement;
CREATE SCHEMA IF NOT EXISTS game_record;
CREATE SCHEMA IF NOT EXISTS replay;
CREATE SCHEMA IF NOT EXISTS leaderboard;
-- 现有 integration Schema 已由 Identity 使用；独占名称可消除跨服务同名表和所有权冲突。
CREATE SCHEMA IF NOT EXISTS game_data_integration;

-- 最终结算不可变权威表；业务幂等键是 match_id + round_no + settlement_version。
CREATE TABLE IF NOT EXISTS settlement.final_results (
    settlement_id UUID PRIMARY KEY,
    match_id VARCHAR(80) NOT NULL,
    room_id VARCHAR(80) NOT NULL,
    round_no INTEGER NOT NULL CHECK (round_no BETWEEN 1 AND 16),
    settlement_version INTEGER NOT NULL CHECK (settlement_version > 0),
    server_instance_id VARCHAR(80) NOT NULL,
    room_epoch BIGINT NOT NULL CHECK (room_epoch > 0),
    ruleset_version VARCHAR(80) NOT NULL,
    server_build VARCHAR(80) NOT NULL,
    final_state_hash CHAR(64) NOT NULL,
    action_log_hash CHAR(64) NOT NULL,
    random_commitment CHAR(64) NOT NULL,
    evidence_id UUID NOT NULL,
    request_fingerprint CHAR(64) NOT NULL,
    envelope JSONB NOT NULL,
    generated_at TIMESTAMPTZ NOT NULL,
    committed_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT ux_settlement_business_key UNIQUE (match_id, round_no, settlement_version)
);
CREATE INDEX IF NOT EXISTS ix_settlement_room
    ON settlement.final_results(room_id, committed_at DESC);

-- 历史修正只能追加补偿记录，禁止覆盖原 FinalResultEnvelope。
CREATE TABLE IF NOT EXISTS settlement.compensations (
    compensation_id UUID PRIMARY KEY,
    settlement_id UUID NOT NULL REFERENCES settlement.final_results(settlement_id),
    reason_code VARCHAR(80) NOT NULL,
    reason TEXT NOT NULL,
    approved_by VARCHAR(128) NOT NULL,
    approved_at TIMESTAMPTZ NOT NULL,
    trace_id VARCHAR(64) NOT NULL,
    ticket_id VARCHAR(128) NOT NULL
);

-- 战绩是结算结果的不可变读模型，不保存钱包余额、奖励或订单。
CREATE TABLE IF NOT EXISTS game_record.matches (
    settlement_id UUID PRIMARY KEY REFERENCES settlement.final_results(settlement_id),
    match_id VARCHAR(80) NOT NULL,
    room_id VARCHAR(80) NOT NULL,
    round_no INTEGER NOT NULL,
    settlement_version INTEGER NOT NULL,
    ruleset_version VARCHAR(80) NOT NULL,
    committed_at TIMESTAMPTZ NOT NULL,
    player_results JSONB NOT NULL,
    CONSTRAINT ux_game_record_business_key UNIQUE (match_id, round_no, settlement_version)
);
CREATE TABLE IF NOT EXISTS game_record.participants (
    match_id VARCHAR(80) NOT NULL,
    round_no INTEGER NOT NULL,
    settlement_version INTEGER NOT NULL,
    player_id VARCHAR(80) NOT NULL,
    seat_id INTEGER NOT NULL CHECK (seat_id BETWEEN 0 AND 3),
    rank INTEGER NOT NULL CHECK (rank BETWEEN 1 AND 4),
    total_score INTEGER NOT NULL,
    PRIMARY KEY (match_id, round_no, settlement_version, player_id),
    FOREIGN KEY (match_id, round_no, settlement_version)
        REFERENCES game_record.matches(match_id, round_no, settlement_version)
);
CREATE INDEX IF NOT EXISTS ix_game_record_player
    ON game_record.participants(player_id, match_id, round_no DESC);

-- 证据目录只保存受控对象位置和摘要，不把完整私有牌复制到普通日志或管理列表。
CREATE TABLE IF NOT EXISTS replay.evidence_manifests (
    evidence_id UUID PRIMARY KEY,
    match_id VARCHAR(80) NOT NULL,
    room_epoch BIGINT NOT NULL CHECK (room_epoch > 0),
    round_no INTEGER NOT NULL CHECK (round_no BETWEEN 1 AND 16),
    settlement_version INTEGER NOT NULL CHECK (settlement_version > 0),
    final_state_hash CHAR(64) NOT NULL,
    action_log_hash CHAR(64) NOT NULL,
    random_commitment CHAR(64) NOT NULL,
    objects JSONB NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    retain_until TIMESTAMPTZ NOT NULL,
    CONSTRAINT ux_replay_business_key UNIQUE (match_id, round_no, settlement_version)
);

-- 基础排行榜可由 SettlementCommitted 重建，不能反向成为战绩权威来源。
CREATE TABLE IF NOT EXISTS leaderboard.player_scores (
    player_id VARCHAR(80) PRIMARY KEY,
    total_score BIGINT NOT NULL,
    match_count BIGINT NOT NULL CHECK (match_count >= 0),
    updated_at TIMESTAMPTZ NOT NULL
);

-- Outbox 与结算业务事务同事务写入；后续发布器按状态和租约领取。
CREATE TABLE IF NOT EXISTS game_data_integration.platform_outbox (
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
CREATE INDEX IF NOT EXISTS ix_game_data_outbox_dispatch
    ON game_data_integration.platform_outbox(status, next_attempt_at, lease_expires_at);

-- Worker 归档只移动已确认发布记录；业务结算和战绩表不参与该清理事务。
CREATE TABLE IF NOT EXISTS game_data_integration.platform_outbox_archive
    (LIKE game_data_integration.platform_outbox INCLUDING ALL);

-- 权威结算、战绩、证据和补偿历史只允许追加；治理删除必须停写、备份并由迁移身份执行。
CREATE OR REPLACE FUNCTION settlement.reject_immutable_mutation()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'GameData immutable history cannot be updated, deleted, or truncated';
END;
$$;

DROP TRIGGER IF EXISTS trg_final_results_immutable ON settlement.final_results;
CREATE TRIGGER trg_final_results_immutable
BEFORE UPDATE OR DELETE OR TRUNCATE ON settlement.final_results
FOR EACH STATEMENT EXECUTE FUNCTION settlement.reject_immutable_mutation();
DROP TRIGGER IF EXISTS trg_compensations_immutable ON settlement.compensations;
CREATE TRIGGER trg_compensations_immutable
BEFORE UPDATE OR DELETE OR TRUNCATE ON settlement.compensations
FOR EACH STATEMENT EXECUTE FUNCTION settlement.reject_immutable_mutation();
DROP TRIGGER IF EXISTS trg_game_records_immutable ON game_record.matches;
CREATE TRIGGER trg_game_records_immutable
BEFORE UPDATE OR DELETE OR TRUNCATE ON game_record.matches
FOR EACH STATEMENT EXECUTE FUNCTION settlement.reject_immutable_mutation();
DROP TRIGGER IF EXISTS trg_evidence_immutable ON replay.evidence_manifests;
CREATE TRIGGER trg_evidence_immutable
BEFORE UPDATE OR DELETE OR TRUNCATE ON replay.evidence_manifests
FOR EACH STATEMENT EXECUTE FUNCTION settlement.reject_immutable_mutation();
