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
    version INTEGER NOT NULL DEFAULT 1
);

CREATE INDEX IF NOT EXISTS ix_admin_action_requests_status_requested
    ON admin_monitor.action_requests(status, requested_at_utc DESC);

ALTER TABLE admin_monitor.action_requests
    ADD COLUMN IF NOT EXISTS confirmation_expires_at_utc TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS confirmed_at_utc TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS expected_state_hash CHAR(64),
    ADD COLUMN IF NOT EXISTS version INTEGER NOT NULL DEFAULT 1;

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

-- The audit-ledger runtime role receives INSERT and SELECT only. UPDATE/DELETE
-- are intentionally absent so management history is append-only at the database boundary.
