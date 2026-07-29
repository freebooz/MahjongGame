CREATE SCHEMA IF NOT EXISTS admin_monitor;

CREATE TABLE IF NOT EXISTS admin_monitor.action_requests (
    action_request_id UUID PRIMARY KEY,
    action_type VARCHAR(64) NOT NULL,
    target_type VARCHAR(32) NOT NULL,
    target_id VARCHAR(128) NOT NULL,
    requested_by VARCHAR(128) NOT NULL,
    requested_at_utc TIMESTAMPTZ NOT NULL,
    confirmation_expires_at_utc TIMESTAMPTZ NOT NULL,
    confirmed_at_utc TIMESTAMPTZ,
    reason TEXT NOT NULL,
    ticket_id VARCHAR(128) NOT NULL,
    trace_id VARCHAR(64) NOT NULL,
    expected_state_sequence BIGINT,
    expected_state_hash CHAR(64) NOT NULL,
    before_state JSONB NOT NULL,
    status VARCHAR(32) NOT NULL,
    expires_at_utc TIMESTAMPTZ NOT NULL,
    version INTEGER NOT NULL DEFAULT 1,
    action_parameters JSONB
);

CREATE INDEX IF NOT EXISTS ix_admin_action_requests_status_requested
    ON admin_monitor.action_requests(status, requested_at_utc DESC);

ALTER TABLE admin_monitor.action_requests
    ADD COLUMN IF NOT EXISTS confirmation_expires_at_utc TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS confirmed_at_utc TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS expected_state_hash CHAR(64),
    ADD COLUMN IF NOT EXISTS version INTEGER NOT NULL DEFAULT 1,
    ADD COLUMN IF NOT EXISTS action_parameters JSONB;

UPDATE admin_monitor.action_requests
SET confirmation_expires_at_utc =
        COALESCE(confirmation_expires_at_utc, requested_at_utc + INTERVAL '5 minutes'),
    expected_state_hash =
        COALESCE(expected_state_hash, repeat('0', 64));

ALTER TABLE admin_monitor.action_requests
    ALTER COLUMN confirmation_expires_at_utc SET NOT NULL,
    ALTER COLUMN expected_state_hash SET NOT NULL;

CREATE TABLE IF NOT EXISTS admin_monitor.action_approvals (
    approval_id UUID PRIMARY KEY,
    action_request_id UUID NOT NULL REFERENCES admin_monitor.action_requests(action_request_id),
    approved_by VARCHAR(128) NOT NULL,
    approved_at_utc TIMESTAMPTZ NOT NULL,
    decision VARCHAR(16) NOT NULL,
    comment TEXT NOT NULL,
    CONSTRAINT ck_no_self_approval CHECK (approved_by <> '')
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_admin_action_approval_request
    ON admin_monitor.action_approvals(action_request_id);

CREATE OR REPLACE FUNCTION admin_monitor.reject_self_approval()
RETURNS TRIGGER AS $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM admin_monitor.action_requests request
        WHERE request.action_request_id = NEW.action_request_id
          AND request.requested_by = NEW.approved_by
    ) THEN
        RAISE EXCEPTION 'The requester cannot approve the same action request';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_reject_self_approval
    ON admin_monitor.action_approvals;
CREATE TRIGGER trg_reject_self_approval
    BEFORE INSERT ON admin_monitor.action_approvals
    FOR EACH ROW EXECUTE FUNCTION admin_monitor.reject_self_approval();

CREATE TABLE IF NOT EXISTS admin_monitor.audit_ledger (
    audit_id UUID PRIMARY KEY,
    sequence BIGINT GENERATED ALWAYS AS IDENTITY UNIQUE,
    occurred_at_utc TIMESTAMPTZ NOT NULL,
    operator_id VARCHAR(128) NOT NULL,
    operation VARCHAR(64) NOT NULL,
    target_type VARCHAR(32) NOT NULL,
    target_id VARCHAR(128) NOT NULL,
    reason TEXT NOT NULL,
    before_state JSONB,
    after_state JSONB,
    approval_record JSONB,
    trace_id VARCHAR(64) NOT NULL,
    ticket_id VARCHAR(128) NOT NULL,
    previous_hash CHAR(64),
    record_hash CHAR(64) NOT NULL UNIQUE
);

CREATE INDEX IF NOT EXISTS ix_admin_audit_target
    ON admin_monitor.audit_ledger(target_type, target_id, occurred_at_utc DESC);

CREATE OR REPLACE FUNCTION admin_monitor.prevent_audit_mutation()
RETURNS TRIGGER AS $$
BEGIN
    RAISE EXCEPTION 'admin_monitor.audit_ledger is append-only';
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_prevent_audit_update_delete
    ON admin_monitor.audit_ledger;
CREATE TRIGGER trg_prevent_audit_update_delete
    BEFORE UPDATE OR DELETE ON admin_monitor.audit_ledger
    FOR EACH ROW EXECUTE FUNCTION admin_monitor.prevent_audit_mutation();

DROP TRIGGER IF EXISTS trg_prevent_audit_truncate
    ON admin_monitor.audit_ledger;
CREATE TRIGGER trg_prevent_audit_truncate
    BEFORE TRUNCATE ON admin_monitor.audit_ledger
    FOR EACH STATEMENT EXECUTE FUNCTION admin_monitor.prevent_audit_mutation();

CREATE TABLE IF NOT EXISTS admin_monitor.audit_archive_outbox (
    audit_id UUID PRIMARY KEY
        REFERENCES admin_monitor.audit_ledger(audit_id),
    payload JSONB NOT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'Pending',
    attempt_count INTEGER NOT NULL DEFAULT 0,
    available_at_utc TIMESTAMPTZ NOT NULL,
    lock_owner VARCHAR(128),
    lease_expires_at_utc TIMESTAMPTZ,
    archived_at_utc TIMESTAMPTZ,
    last_error TEXT,
    CONSTRAINT ck_admin_audit_archive_status
        CHECK (status IN ('Pending', 'Processing', 'Archived', 'Failed'))
);

CREATE INDEX IF NOT EXISTS ix_admin_audit_archive_dispatch
    ON admin_monitor.audit_archive_outbox(status, available_at_utc);

CREATE OR REPLACE FUNCTION admin_monitor.enqueue_audit_archive()
RETURNS TRIGGER AS $$
BEGIN
    INSERT INTO admin_monitor.audit_archive_outbox(
        audit_id, payload, available_at_utc)
    VALUES (
        NEW.audit_id,
        jsonb_build_object(
            'auditId', NEW.audit_id,
            'sequence', NEW.sequence,
            'occurredAtUtc', NEW.occurred_at_utc,
            'operatorId', NEW.operator_id,
            'operation', NEW.operation,
            'targetType', NEW.target_type,
            'targetId', NEW.target_id,
            'reason', NEW.reason,
            'beforeState', NEW.before_state,
            'afterState', NEW.after_state,
            'approvalRecord', NEW.approval_record,
            'traceId', NEW.trace_id,
            'ticketId', NEW.ticket_id,
            'previousHash', NEW.previous_hash,
            'recordHash', NEW.record_hash),
        NEW.occurred_at_utc)
    ON CONFLICT (audit_id) DO NOTHING;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_enqueue_audit_archive
    ON admin_monitor.audit_ledger;
CREATE TRIGGER trg_enqueue_audit_archive
    AFTER INSERT ON admin_monitor.audit_ledger
    FOR EACH ROW EXECUTE FUNCTION admin_monitor.enqueue_audit_archive();

INSERT INTO admin_monitor.audit_archive_outbox(
    audit_id, payload, available_at_utc)
SELECT
    audit_id,
    jsonb_build_object(
        'auditId', audit_id,
        'sequence', sequence,
        'occurredAtUtc', occurred_at_utc,
        'operatorId', operator_id,
        'operation', operation,
        'targetType', target_type,
        'targetId', target_id,
        'reason', reason,
        'beforeState', before_state,
        'afterState', after_state,
        'approvalRecord', approval_record,
        'traceId', trace_id,
        'ticketId', ticket_id,
        'previousHash', previous_hash,
        'recordHash', record_hash),
    occurred_at_utc
FROM admin_monitor.audit_ledger
ON CONFLICT (audit_id) DO NOTHING;

CREATE TABLE IF NOT EXISTS admin_monitor.command_outbox (
    outbox_id UUID PRIMARY KEY,
    action_request_id UUID NOT NULL UNIQUE
        REFERENCES admin_monitor.action_requests(action_request_id),
    action_type VARCHAR(64) NOT NULL,
    target_type VARCHAR(32) NOT NULL,
    target_id VARCHAR(128) NOT NULL,
    payload JSONB NOT NULL,
    trace_id VARCHAR(64) NOT NULL,
    status VARCHAR(32) NOT NULL,
    attempt_count INTEGER NOT NULL DEFAULT 0,
    available_at_utc TIMESTAMPTZ NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL,
    locked_at_utc TIMESTAMPTZ,
    lock_owner VARCHAR(128),
    lease_expires_at_utc TIMESTAMPTZ,
    completed_at_utc TIMESTAMPTZ,
    last_error TEXT
);

CREATE INDEX IF NOT EXISTS ix_admin_command_outbox_dispatch
    ON admin_monitor.command_outbox(status, available_at_utc);

ALTER TABLE admin_monitor.command_outbox
    ADD COLUMN IF NOT EXISTS lock_owner VARCHAR(128),
    ADD COLUMN IF NOT EXISTS lease_expires_at_utc TIMESTAMPTZ;

CREATE TABLE IF NOT EXISTS admin_monitor.management_cases (
    case_id UUID PRIMARY KEY,
    source_command_id UUID NOT NULL UNIQUE,
    action_request_id UUID NOT NULL UNIQUE
        REFERENCES admin_monitor.action_requests(action_request_id),
    case_type VARCHAR(32) NOT NULL,
    target_type VARCHAR(32) NOT NULL,
    target_id VARCHAR(128) NOT NULL,
    requested_by VARCHAR(128) NOT NULL,
    approved_by VARCHAR(128) NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL,
    reason TEXT NOT NULL,
    ticket_id VARCHAR(128) NOT NULL,
    trace_id VARCHAR(64) NOT NULL,
    before_state JSONB NOT NULL,
    status VARCHAR(24) NOT NULL,
    CONSTRAINT ck_admin_case_separate_approval
        CHECK (requested_by <> approved_by),
    CONSTRAINT ck_admin_case_type
        CHECK (case_type IN (
            'DisputeInvestigation',
            'PlayerSupport',
            'CompensationReview',
            'ReplayReview',
            'RoomLogExport')),
    CONSTRAINT ck_admin_case_status CHECK (status IN ('Open', 'Closed'))
);

CREATE INDEX IF NOT EXISTS ix_admin_management_cases_target
    ON admin_monitor.management_cases(
        target_type, target_id, created_at_utc DESC);

ALTER TABLE admin_monitor.management_cases
    ADD COLUMN IF NOT EXISTS closed_at_utc TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS closed_by VARCHAR(128),
    ADD COLUMN IF NOT EXISTS resolution TEXT,
    ADD COLUMN IF NOT EXISTS evidence_package_hash CHAR(64);

-- 兼容尚未记录闭环字段的早期 Closed 数据；固定迁移摘要只标识“旧记录”，不冒充真实证据包。
UPDATE admin_monitor.management_cases
SET closed_at_utc = COALESCE(closed_at_utc, created_at_utc),
    closed_by = COALESCE(closed_by, approved_by),
    resolution = COALESCE(
        resolution,
        'Migrated closed case; historical resolution was unavailable.'),
    evidence_package_hash = COALESCE(
        evidence_package_hash,
        repeat('0', 64))
WHERE status = 'Closed';

ALTER TABLE admin_monitor.management_cases
    DROP CONSTRAINT IF EXISTS ck_admin_case_closure;
ALTER TABLE admin_monitor.management_cases
    ADD CONSTRAINT ck_admin_case_closure CHECK (
        (status = 'Open'
            AND closed_at_utc IS NULL
            AND closed_by IS NULL
            AND resolution IS NULL
            AND evidence_package_hash IS NULL)
        OR
        (status = 'Closed'
            AND closed_at_utc IS NOT NULL
            AND closed_by IS NOT NULL
            AND char_length(resolution) BETWEEN 10 AND 2000
            AND evidence_package_hash ~ '^[0-9a-f]{64}$'));

ALTER TABLE admin_monitor.management_cases
    DROP CONSTRAINT IF EXISTS ck_admin_case_type;
ALTER TABLE admin_monitor.management_cases
    ADD CONSTRAINT ck_admin_case_type CHECK (case_type IN (
        'DisputeInvestigation',
        'PlayerSupport',
        'CompensationReview',
        'ReplayReview',
        'RoomLogExport'));

CREATE TABLE IF NOT EXISTS admin_monitor.player_asset_operations (
    operation_id UUID PRIMARY KEY,
    source_command_id UUID NOT NULL UNIQUE,
    action_request_id UUID NOT NULL UNIQUE
        REFERENCES admin_monitor.action_requests(action_request_id),
    case_id UUID NOT NULL
        REFERENCES admin_monitor.management_cases(case_id),
    operation_type VARCHAR(32) NOT NULL,
    player_id VARCHAR(128) NOT NULL,
    asset_code VARCHAR(32),
    amount BIGINT,
    reward_grant_id VARCHAR(128),
    requested_by VARCHAR(128) NOT NULL,
    approved_by VARCHAR(128) NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL,
    reason TEXT NOT NULL,
    ticket_id VARCHAR(128) NOT NULL,
    trace_id VARCHAR(64) NOT NULL,
    before_state JSONB NOT NULL,
    status VARCHAR(48) NOT NULL,
    CONSTRAINT ck_admin_asset_separate_approval
        CHECK (requested_by <> approved_by),
    CONSTRAINT ck_admin_asset_operation_type
        CHECK (operation_type IN ('GrantCompensation', 'RevokeReward')),
    CONSTRAINT ck_admin_asset_operation_payload CHECK (
        (operation_type = 'GrantCompensation'
            AND asset_code IS NOT NULL
            AND amount > 0
            AND reward_grant_id IS NULL)
        OR
        (operation_type = 'RevokeReward'
            AND asset_code IS NULL
            AND amount IS NULL
            AND reward_grant_id IS NOT NULL)),
    CONSTRAINT ck_admin_asset_operation_status
        CHECK (status IN (
            'ApprovedPendingWalletExecution',
            'WalletCompleted',
            'WalletRejected'))
);

CREATE INDEX IF NOT EXISTS ix_admin_player_asset_operations_player
    ON admin_monitor.player_asset_operations(
        player_id, created_at_utc DESC);

CREATE TABLE IF NOT EXISTS admin_monitor.player_evidence (
    event_id UUID PRIMARY KEY,
    player_id VARCHAR(128) NOT NULL,
    evidence_type VARCHAR(32) NOT NULL,
    occurred_at_utc TIMESTAMPTZ NOT NULL,
    source_reference VARCHAR(128) NOT NULL,
    data JSONB NOT NULL,
    sensitivity VARCHAR(16) NOT NULL,
    ingested_at_utc TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_admin_player_evidence_type CHECK (
        evidence_type IN (
            'Report',
            'AssetChange',
            'RewardClaim',
            'PaymentOrder',
            'Replay')),
    CONSTRAINT ck_admin_player_evidence_sensitivity CHECK (
        sensitivity IN ('Operational', 'Restricted', 'Financial')),
    CONSTRAINT ux_admin_player_evidence_source
        UNIQUE (evidence_type, source_reference)
);

CREATE INDEX IF NOT EXISTS ix_admin_player_evidence_player
    ON admin_monitor.player_evidence(
        player_id, evidence_type, occurred_at_utc DESC);

CREATE TABLE IF NOT EXISTS admin_monitor.player_chat_access_grants (
    grant_id UUID PRIMARY KEY,
    player_id VARCHAR(128) NOT NULL,
    ticket_id VARCHAR(128) NOT NULL,
    granted_to VARCHAR(128) NOT NULL,
    approved_by VARCHAR(128) NOT NULL,
    reason TEXT NOT NULL,
    trace_id VARCHAR(64) NOT NULL,
    window_starts_at_utc TIMESTAMPTZ NOT NULL,
    window_ends_at_utc TIMESTAMPTZ NOT NULL,
    expires_at_utc TIMESTAMPTZ NOT NULL,
    scopes TEXT[] NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_admin_chat_grant_separate_approval
        CHECK (granted_to <> approved_by),
    CONSTRAINT ck_admin_chat_grant_window
        CHECK (
            window_starts_at_utc < window_ends_at_utc
            AND created_at_utc < expires_at_utc)
);

ALTER TABLE admin_monitor.player_chat_access_grants
    ADD COLUMN IF NOT EXISTS reason TEXT,
    ADD COLUMN IF NOT EXISTS trace_id VARCHAR(64);
UPDATE admin_monitor.player_chat_access_grants
SET reason = COALESCE(reason, 'Migrated legacy chat access grant'),
    trace_id = COALESCE(trace_id, 'legacy-migration');
ALTER TABLE admin_monitor.player_chat_access_grants
    ALTER COLUMN reason SET NOT NULL,
    ALTER COLUMN trace_id SET NOT NULL;

CREATE INDEX IF NOT EXISTS ix_admin_chat_grant_lookup
    ON admin_monitor.player_chat_access_grants(
        player_id, ticket_id, granted_to, expires_at_utc DESC);

-- The audit-ledger runtime role receives INSERT and SELECT only. UPDATE/DELETE
-- are intentionally absent so management history is append-only at the database boundary.
