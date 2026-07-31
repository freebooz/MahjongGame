CREATE SCHEMA IF NOT EXISTS player_data;

CREATE TABLE IF NOT EXISTS player_data.wallet_balances (
    player_id VARCHAR(128) NOT NULL,
    asset_code VARCHAR(32) NOT NULL,
    balance BIGINT NOT NULL,
    version BIGINT NOT NULL,
    updated_at_utc TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (player_id, asset_code),
    CONSTRAINT ck_wallet_balance_non_negative CHECK (balance >= 0)
);

CREATE TABLE IF NOT EXISTS player_data.reward_grants (
    reward_grant_id VARCHAR(128) PRIMARY KEY,
    player_id VARCHAR(128) NOT NULL,
    asset_code VARCHAR(32) NOT NULL,
    amount BIGINT NOT NULL,
    status VARCHAR(16) NOT NULL,
    claimed_at_utc TIMESTAMPTZ NOT NULL,
    revoked_at_utc TIMESTAMPTZ,
    CONSTRAINT ck_reward_amount_positive CHECK (amount > 0),
    CONSTRAINT ck_reward_status CHECK (status IN ('Claimed', 'Revoked'))
);

CREATE INDEX IF NOT EXISTS ix_reward_grants_player
    ON player_data.reward_grants(player_id, claimed_at_utc DESC);

CREATE TABLE IF NOT EXISTS player_data.wallet_transactions (
    transaction_id UUID PRIMARY KEY,
    command_id UUID NOT NULL UNIQUE,
    operation_type VARCHAR(32) NOT NULL,
    player_id VARCHAR(128) NOT NULL,
    asset_code VARCHAR(32) NOT NULL,
    amount BIGINT NOT NULL,
    balance_after BIGINT NOT NULL,
    balance_version BIGINT NOT NULL,
    request_data JSONB NOT NULL,
    case_id UUID NOT NULL,
    requested_by VARCHAR(128) NOT NULL,
    approved_by VARCHAR(128) NOT NULL,
    reason TEXT NOT NULL,
    ticket_id VARCHAR(128) NOT NULL,
    trace_id VARCHAR(64) NOT NULL,
    completed_at_utc TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_wallet_transaction_approval
        CHECK (requested_by <> approved_by),
    CONSTRAINT ck_wallet_transaction_type
        CHECK (operation_type IN ('GrantCompensation', 'RevokeReward'))
);

CREATE INDEX IF NOT EXISTS ix_wallet_transactions_player
    ON player_data.wallet_transactions(player_id, completed_at_utc DESC);

CREATE TABLE IF NOT EXISTS player_data.evidence_events (
    event_id UUID PRIMARY KEY,
    player_id VARCHAR(128) NOT NULL,
    evidence_type VARCHAR(32) NOT NULL,
    occurred_at_utc TIMESTAMPTZ NOT NULL,
    source_reference VARCHAR(128) NOT NULL,
    data JSONB NOT NULL,
    sensitivity VARCHAR(16) NOT NULL,
    recorded_at_utc TIMESTAMPTZ NOT NULL,
    CONSTRAINT ux_player_data_evidence_source
        UNIQUE (evidence_type, source_reference),
    CONSTRAINT ck_player_data_evidence_type CHECK (
        evidence_type IN (
            'Report',
            'AssetChange',
            'RewardClaim',
            'PaymentOrder',
            'Replay')),
    CONSTRAINT ck_player_data_evidence_sensitivity CHECK (
        sensitivity IN ('Restricted', 'Financial'))
);

CREATE INDEX IF NOT EXISTS ix_player_data_evidence_player
    ON player_data.evidence_events(
        player_id, evidence_type, occurred_at_utc DESC);

-- 阶段8.2切换后Replay由GameData独占；数据库门禁防止遗留代码绕过兼容适配器继续写旧表。
CREATE OR REPLACE FUNCTION player_data.reject_replay_evidence_write()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.evidence_type = 'Replay' THEN
        RAISE EXCEPTION 'Replay evidence is owned by GameData after stage 8.2';
    END IF;
    RETURN NEW;
END;
$$;
DROP TRIGGER IF EXISTS trg_reject_replay_evidence_write ON player_data.evidence_events;
CREATE TRIGGER trg_reject_replay_evidence_write
BEFORE INSERT OR UPDATE ON player_data.evidence_events
FOR EACH ROW EXECUTE FUNCTION player_data.reject_replay_evidence_write();

CREATE TABLE IF NOT EXISTS player_data.projection_outbox (
    event_id UUID PRIMARY KEY
        REFERENCES player_data.evidence_events(event_id),
    payload JSONB NOT NULL,
    status VARCHAR(16) NOT NULL,
    attempt_count INTEGER NOT NULL DEFAULT 0,
    available_at_utc TIMESTAMPTZ NOT NULL,
    lock_owner VARCHAR(128),
    lease_expires_at_utc TIMESTAMPTZ,
    last_error TEXT,
    CONSTRAINT ck_projection_status CHECK (
        status IN ('Pending', 'Processing', 'Completed', 'Failed'))
);

CREATE INDEX IF NOT EXISTS ix_player_data_projection_dispatch
    ON player_data.projection_outbox(status, available_at_utc);
