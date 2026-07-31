\set ON_ERROR_STOP on

BEGIN;

-- 回滚到阶段 3 之前的存储布局。新玩家档案、设备切换和未发布 Outbox 数据
-- 不被旧版本读取，因此在显式回滚时删除；执行前必须按发布流程完成备份。
DROP TABLE IF EXISTS integration.identity_outbox;
DROP TABLE IF EXISTS integration.auth_device_switch_events;
DROP TABLE IF EXISTS integration.auth_devices;
DROP TABLE IF EXISTS player.player_profiles;

ALTER TABLE session.auth_refresh_sessions
    DROP COLUMN IF EXISTS family_id,
    DROP COLUMN IF EXISTS parent_session_id,
    DROP COLUMN IF EXISTS device_id,
    DROP COLUMN IF EXISTS session_epoch,
    DROP COLUMN IF EXISTS security_epoch,
    DROP COLUMN IF EXISTS replaced_by_session_id,
    DROP COLUMN IF EXISTS revocation_reason,
    DROP COLUMN IF EXISTS reuse_detected_at_utc;

ALTER TABLE auth.auth_identities
    DROP COLUMN IF EXISTS session_epoch,
    DROP COLUMN IF EXISTS security_epoch;

ALTER TABLE session.auth_refresh_sessions SET SCHEMA public;
ALTER TABLE integration.auth_login_events SET SCHEMA public;
ALTER TABLE auth.auth_admin_commands SET SCHEMA public;
ALTER TABLE auth.auth_player_controls SET SCHEMA public;
ALTER TABLE auth.auth_player_control_events SET SCHEMA public;
ALTER TABLE auth.auth_identities SET SCHEMA public;

DROP SCHEMA IF EXISTS integration;
DROP SCHEMA IF EXISTS player;
DROP SCHEMA IF EXISTS session;
DROP SCHEMA IF EXISTS auth;

COMMIT;
