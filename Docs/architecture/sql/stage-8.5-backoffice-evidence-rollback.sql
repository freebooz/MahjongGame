-- 回滚仅恢复旧类型写能力；Admin证据是调查审计记录，不得删除。
BEGIN;
DROP TRIGGER IF EXISTS trg_reject_migrated_backoffice_evidence_write
    ON player_data.evidence_events;
COMMIT;
