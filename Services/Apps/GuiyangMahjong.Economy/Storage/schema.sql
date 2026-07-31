CREATE SCHEMA IF NOT EXISTS inventory;
CREATE SCHEMA IF NOT EXISTS reward;
CREATE SCHEMA IF NOT EXISTS economy_integration;

-- 余额是 Inventory 的权威当前态，只允许通过增量交易修改，禁止直接设置最终值。
CREATE TABLE IF NOT EXISTS inventory.wallet_balances (
    player_id VARCHAR(128) NOT NULL,
    asset_code VARCHAR(32) NOT NULL,
    balance BIGINT NOT NULL CHECK (balance >= 0),
    version BIGINT NOT NULL CHECK (version > 0),
    updated_at_utc TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (player_id, asset_code)
);

-- 奖励领取由 EventId 和 RewardGrantId 双重幂等；状态只能由受审计撤销命令改变。
CREATE TABLE IF NOT EXISTS reward.reward_grants (
    reward_grant_id VARCHAR(128) PRIMARY KEY,
    source_event_id UUID NOT NULL UNIQUE,
    source_reference VARCHAR(128) NOT NULL UNIQUE,
    player_id VARCHAR(128) NOT NULL,
    asset_code VARCHAR(32) NOT NULL,
    amount BIGINT NOT NULL CHECK (amount > 0),
    status VARCHAR(16) NOT NULL CHECK (status IN ('Claimed', 'Revoked')),
    trace_id VARCHAR(64) NOT NULL,
    claimed_at_utc TIMESTAMPTZ NOT NULL,
    revoked_at_utc TIMESTAMPTZ
);
CREATE INDEX IF NOT EXISTS ix_economy_rewards_player
    ON reward.reward_grants(player_id, claimed_at_utc DESC);

-- 交易流水不可变；request_data 用于检测同一幂等键是否被不同参数复用。
CREATE TABLE IF NOT EXISTS inventory.wallet_transactions (
    transaction_id UUID PRIMARY KEY,
    command_id UUID NOT NULL UNIQUE,
    operation_type VARCHAR(32) NOT NULL CHECK (operation_type IN ('GrantCompensation', 'RevokeReward')),
    player_id VARCHAR(128) NOT NULL,
    asset_code VARCHAR(32) NOT NULL,
    amount BIGINT NOT NULL,
    balance_after BIGINT NOT NULL CHECK (balance_after >= 0),
    balance_version BIGINT NOT NULL,
    request_data JSONB NOT NULL,
    case_id UUID NOT NULL,
    requested_by VARCHAR(128) NOT NULL,
    approved_by VARCHAR(128) NOT NULL,
    reason TEXT NOT NULL,
    ticket_id VARCHAR(128) NOT NULL,
    trace_id VARCHAR(64) NOT NULL,
    completed_at_utc TIMESTAMPTZ NOT NULL,
    CHECK (requested_by <> approved_by)
);
CREATE INDEX IF NOT EXISTS ix_economy_transactions_player
    ON inventory.wallet_transactions(player_id, completed_at_utc DESC);

-- 业务事实与余额事务同库提交；阶段 9 Worker 负责可靠发布，失败不回滚已提交资产。
CREATE TABLE IF NOT EXISTS economy_integration.platform_outbox (
    event_id UUID PRIMARY KEY,
    event_type VARCHAR(128) NOT NULL,
    schema_version INTEGER NOT NULL,
    payload JSONB NOT NULL,
    occurred_at_utc TIMESTAMPTZ NOT NULL,
    status VARCHAR(16) NOT NULL DEFAULT 'Pending',
    attempt_count INTEGER NOT NULL DEFAULT 0,
    available_at_utc TIMESTAMPTZ NOT NULL,
    last_error TEXT
);
CREATE INDEX IF NOT EXISTS ix_economy_outbox_dispatch
    ON economy_integration.platform_outbox(status, available_at_utc);
