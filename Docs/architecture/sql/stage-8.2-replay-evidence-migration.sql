-- 阶段8.2数据迁移：仅复制PlayerData中Replay类型元数据到GameData拥有的兼容索引。
-- 执行前必须已经应用GameData schema.sql；脚本可重复执行，不修改或删除源表。
BEGIN;

INSERT INTO replay.legacy_player_evidence(
    event_id, player_id, occurred_at, source_reference, data, sensitivity,
    request_fingerprint, recorded_at)
SELECT
    event_id,
    player_id,
    occurred_at_utc,
    source_reference,
    data,
    sensitivity,
    encode(sha256(convert_to(
        event_id::text || E'\n' || player_id || E'\n' ||
        to_char(occurred_at_utc AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"') || E'\n' ||
        source_reference || E'\n' || data::text || E'\n' || sensitivity,
        'UTF8')), 'hex'),
    recorded_at_utc
FROM player_data.evidence_events
WHERE evidence_type = 'Replay'
ON CONFLICT DO NOTHING;

-- 任一源Replay未出现在目标中都必须使迁移失败并回滚，禁止带缺口切换写入口。
DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM player_data.evidence_events source
        LEFT JOIN replay.legacy_player_evidence target ON target.event_id = source.event_id
        WHERE source.evidence_type = 'Replay' AND target.event_id IS NULL)
    THEN
        RAISE EXCEPTION 'stage 8.2 replay migration is incomplete';
    END IF;
END $$;

-- 数据核对成功后在同一事务关闭旧Replay写入；其他证据类型不受影响。
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

COMMIT;
