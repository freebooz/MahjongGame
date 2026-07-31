-- 回滚前必须停止全部 Durable Consumer，并确认保留期和调查工单已经审批。
DROP TABLE IF EXISTS worker_projection.audit_events;
DROP TABLE IF EXISTS worker_projection.leaderboard_updates;
DROP TABLE IF EXISTS worker_projection.game_records;
DROP TABLE IF EXISTS worker_integration.projection_checkpoints;
DROP TABLE IF EXISTS worker_integration.failed_events;
DROP TABLE IF EXISTS worker_integration.platform_inbox;
DROP SCHEMA IF EXISTS worker_projection;
DROP SCHEMA IF EXISTS worker_integration;
