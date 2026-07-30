-- 工作流 E：对象所有权和最小授权。应在四个服务 schema.sql 全部执行成功后运行。
-- 先撤销 PUBLIC，再逐表授权，避免 public schema 中 Auth 与 Lobby 互相越权。

REVOKE CREATE ON SCHEMA public FROM PUBLIC;
REVOKE ALL ON SCHEMA player_data, admin_monitor FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO
    mahjong_auth_rw, mahjong_lobby_rw, mahjong_monitor_ro;
GRANT USAGE ON SCHEMA player_data TO
    mahjong_player_data_rw, mahjong_monitor_ro;
GRANT USAGE ON SCHEMA admin_monitor TO
    mahjong_admin_rw, mahjong_monitor_ro,
    mahjong_audit_append, mahjong_archive_dispatch;
GRANT CREATE, USAGE ON SCHEMA public TO mahjong_migration;
ALTER SCHEMA player_data OWNER TO mahjong_migration;
ALTER SCHEMA admin_monitor OWNER TO mahjong_migration;

REVOKE ALL ON TABLE
    auth_identities, auth_refresh_sessions, auth_login_events,
    auth_admin_commands, auth_player_controls, auth_player_control_events,
    lobby_rooms, active_player_rooms, match_results,
    room_event_history, player_room_history, player_connection_history
FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA player_data, admin_monitor FROM PUBLIC;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA admin_monitor FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA admin_monitor FROM PUBLIC;

ALTER TABLE auth_identities OWNER TO mahjong_migration;
ALTER TABLE auth_refresh_sessions OWNER TO mahjong_migration;
ALTER TABLE auth_login_events OWNER TO mahjong_migration;
ALTER TABLE auth_admin_commands OWNER TO mahjong_migration;
ALTER TABLE auth_player_controls OWNER TO mahjong_migration;
ALTER TABLE auth_player_control_events OWNER TO mahjong_migration;
ALTER TABLE lobby_rooms OWNER TO mahjong_migration;
ALTER TABLE active_player_rooms OWNER TO mahjong_migration;
ALTER TABLE match_results OWNER TO mahjong_migration;
ALTER TABLE room_event_history OWNER TO mahjong_migration;
ALTER TABLE player_room_history OWNER TO mahjong_migration;
ALTER TABLE player_connection_history OWNER TO mahjong_migration;

DO $ownership$
DECLARE
    object_record record;
BEGIN
    FOR object_record IN
        SELECT format('%I.%I', schemaname, tablename) AS object_name
        FROM pg_tables
        WHERE schemaname IN ('player_data', 'admin_monitor')
    LOOP
        EXECUTE format('ALTER TABLE %s OWNER TO mahjong_migration', object_record.object_name);
    END LOOP;
    FOR object_record IN
        SELECT format('%I.%I', sequence_schema, sequence_name) AS object_name
        FROM information_schema.sequences
        WHERE sequence_schema IN ('player_data', 'admin_monitor')
    LOOP
        EXECUTE format('ALTER SEQUENCE %s OWNER TO mahjong_migration', object_record.object_name);
    END LOOP;
    FOR object_record IN
        SELECT p.oid::regprocedure AS object_name
        FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname = 'admin_monitor'
    LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO mahjong_migration', object_record.object_name);
    END LOOP;
END
$ownership$;

GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE
    auth_identities, auth_refresh_sessions, auth_login_events,
    auth_admin_commands, auth_player_controls, auth_player_control_events
TO mahjong_auth_rw;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE
    lobby_rooms, active_player_rooms, match_results
TO mahjong_lobby_rw;
-- 房间事件只允许追加；玩家历史由安全定义者触发器投影，运行身份仅负责读取调查结果。
GRANT SELECT, INSERT ON TABLE room_event_history TO mahjong_lobby_rw;
GRANT SELECT ON TABLE player_room_history, player_connection_history
TO mahjong_lobby_rw;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA player_data
TO mahjong_player_data_rw;

GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE
    admin_monitor.action_requests,
    admin_monitor.action_approvals,
    admin_monitor.command_outbox,
    admin_monitor.management_cases,
    admin_monitor.player_asset_operations,
    admin_monitor.player_evidence,
    admin_monitor.player_chat_access_grants
TO mahjong_admin_rw;
GRANT SELECT, INSERT ON TABLE admin_monitor.audit_ledger
TO mahjong_admin_rw, mahjong_audit_append;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA admin_monitor
TO mahjong_admin_rw, mahjong_audit_append;
GRANT SELECT, UPDATE ON TABLE admin_monitor.audit_archive_outbox
TO mahjong_archive_dispatch;

GRANT SELECT ON TABLE
    auth_identities, auth_refresh_sessions, auth_login_events,
    auth_admin_commands, auth_player_controls, auth_player_control_events,
    lobby_rooms, active_player_rooms, match_results,
    room_event_history, player_room_history, player_connection_history
TO mahjong_monitor_ro;
GRANT SELECT ON ALL TABLES IN SCHEMA player_data, admin_monitor
TO mahjong_monitor_ro;

-- 审计入库触发器以对象所有者执行，只允许由审计追加动作间接写入归档 Outbox。
ALTER FUNCTION admin_monitor.enqueue_audit_archive() SECURITY DEFINER;
ALTER FUNCTION admin_monitor.enqueue_audit_archive()
    SET search_path = pg_catalog, admin_monitor;
REVOKE ALL ON FUNCTION admin_monitor.enqueue_audit_archive() FROM PUBLIC;

-- 两个投影函数以迁移所有者执行，使 Lobby 无需获得历史表的任意写权限；固定 search_path 防止对象劫持。
ALTER FUNCTION project_player_room_history() OWNER TO mahjong_migration;
ALTER FUNCTION project_player_room_history() SECURITY DEFINER;
ALTER FUNCTION project_player_room_history()
    SET search_path = pg_catalog, public;
REVOKE ALL ON FUNCTION project_player_room_history() FROM PUBLIC;
ALTER FUNCTION project_player_connection_history() OWNER TO mahjong_migration;
ALTER FUNCTION project_player_connection_history() SECURITY DEFINER;
ALTER FUNCTION project_player_connection_history()
    SET search_path = pg_catalog, public;
REVOKE ALL ON FUNCTION project_player_connection_history() FROM PUBLIC;

-- 后续由 migration 身份创建的对象默认不向 PUBLIC 泄露，并自动继承对应最小权限。
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA player_data
    REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA player_data
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO mahjong_player_data_rw;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA admin_monitor
    REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA admin_monitor
    GRANT SELECT ON TABLES TO mahjong_monitor_ro;
