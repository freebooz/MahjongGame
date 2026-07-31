-- 阶段8.2紧急回滚只恢复PlayerData旧Replay写能力；不会删除GameData已迁记录。
-- 执行前必须先把PlayerData镜像和流量切回阶段8.1版本，避免两个入口同时成为写入者。
BEGIN;
DROP TRIGGER IF EXISTS trg_reject_replay_evidence_write ON player_data.evidence_events;
DROP FUNCTION IF EXISTS player_data.reject_replay_evidence_write();
COMMIT;
