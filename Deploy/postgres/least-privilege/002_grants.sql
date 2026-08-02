-- 工作流 E：对象所有权和最小授权。应在四个服务 schema.sql 全部执行成功后运行。
-- 先撤销 PUBLIC，再逐表授权，避免 public schema 中 Auth 与 Lobby 互相越权。

REVOKE CREATE ON SCHEMA public FROM PUBLIC;
-- PlayerData 已退役，但最小权限清单仍需在全新数据库中冻结其历史命名空间；
-- 必须先幂等创建再参与批量 REVOKE，避免首次部署因 Schema 尚不存在而中断。
CREATE SCHEMA IF NOT EXISTS player_data AUTHORIZATION mahjong_migration;
REVOKE ALL ON SCHEMA
    auth, session, player, integration,
    lobby, room, matchmaking,
    player_data, admin_monitor,
    settlement, game_record, replay, leaderboard, game_data_integration,
    configuration, configuration_integration,
    identity_integration, lobby_integration, worker_integration, worker_projection
FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO
    mahjong_lobby_rw, mahjong_monitor_ro;
-- LobbyControl 的新逻辑 Schema 与旧 public 表并存；运行身份只能使用对象，不能创建或变更结构。
GRANT USAGE ON SCHEMA lobby, room, matchmaking TO
    mahjong_lobby_rw, mahjong_monitor_ro;
GRANT USAGE ON SCHEMA auth, session, player, integration TO
    mahjong_auth_rw, mahjong_monitor_ro;
-- PlayerData 已退役；空环境仅保留只读历史命名空间，升级环境中的旧表由阶段8.6核对后冻结。
GRANT USAGE ON SCHEMA player_data TO mahjong_monitor_ro;
REVOKE ALL ON SCHEMA player_data FROM mahjong_player_data_rw, mahjong_player_data;
-- GameData 生产事务与 Workers 发布事务使用同一 Outbox Schema；Worker 需有 Schema USAGE 才能领取表记录。
GRANT USAGE ON SCHEMA settlement, game_record, replay, leaderboard, game_data_integration TO
    mahjong_game_data_rw, mahjong_workers_rw, mahjong_monitor_ro;
-- Configuration 是独立数据所有者；Worker 只领取其 Outbox，Admin 没有该 Schema 的直接写权限。
GRANT USAGE ON SCHEMA configuration, configuration_integration TO
    mahjong_configuration_rw, mahjong_workers_rw, mahjong_monitor_ro;
-- Producer 只能向自己的事务 Outbox 追加；Worker 只能领取、标记和归档 Outbox，不能读取业务表。
GRANT USAGE ON SCHEMA identity_integration TO mahjong_auth_rw, mahjong_workers_rw, mahjong_monitor_ro;
GRANT USAGE ON SCHEMA lobby_integration TO mahjong_lobby_rw, mahjong_workers_rw, mahjong_monitor_ro;
GRANT USAGE ON SCHEMA worker_integration, worker_projection TO mahjong_workers_rw, mahjong_monitor_ro;
GRANT USAGE ON SCHEMA admin_monitor TO
    mahjong_admin_rw, mahjong_monitor_ro,
    mahjong_audit_append, mahjong_archive_dispatch;
GRANT CREATE, USAGE ON SCHEMA public TO mahjong_migration;
ALTER SCHEMA auth OWNER TO mahjong_migration;
ALTER SCHEMA session OWNER TO mahjong_migration;
ALTER SCHEMA player OWNER TO mahjong_migration;
ALTER SCHEMA integration OWNER TO mahjong_migration;
ALTER SCHEMA lobby OWNER TO mahjong_migration;
ALTER SCHEMA room OWNER TO mahjong_migration;
ALTER SCHEMA matchmaking OWNER TO mahjong_migration;
ALTER SCHEMA player_data OWNER TO mahjong_migration;
ALTER SCHEMA settlement OWNER TO mahjong_migration;
ALTER SCHEMA game_record OWNER TO mahjong_migration;
ALTER SCHEMA replay OWNER TO mahjong_migration;
ALTER SCHEMA leaderboard OWNER TO mahjong_migration;
ALTER SCHEMA game_data_integration OWNER TO mahjong_migration;
ALTER SCHEMA configuration OWNER TO mahjong_migration;
ALTER SCHEMA configuration_integration OWNER TO mahjong_migration;
ALTER SCHEMA identity_integration OWNER TO mahjong_migration;
ALTER SCHEMA lobby_integration OWNER TO mahjong_migration;
ALTER SCHEMA worker_integration OWNER TO mahjong_migration;
ALTER SCHEMA worker_projection OWNER TO mahjong_migration;
ALTER SCHEMA admin_monitor OWNER TO mahjong_migration;

REVOKE ALL ON TABLE
    lobby_rooms, active_player_rooms, match_results,
    room_event_history, player_room_history, player_connection_history
FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA auth, session, player, integration FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA lobby, room, matchmaking FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA player_data, admin_monitor FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA settlement, game_record, replay, leaderboard, game_data_integration FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA configuration, configuration_integration FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA identity_integration, lobby_integration, worker_integration, worker_projection FROM PUBLIC;

-- 事务生产者只允许追加和读取幂等冲突键；PostgreSQL 的 INSERT ... ON CONFLICT
-- 必须具备目标表 SELECT 权限。UPDATE/DELETE 仍仅交给发布 Worker，阻止业务服务伪造发布状态。
GRANT SELECT, INSERT ON identity_integration.platform_outbox TO mahjong_auth_rw;
GRANT SELECT, INSERT ON lobby_integration.platform_outbox TO mahjong_lobby_rw;
GRANT SELECT, UPDATE, DELETE ON identity_integration.platform_outbox,
    lobby_integration.platform_outbox,
    game_data_integration.platform_outbox TO mahjong_workers_rw;
GRANT SELECT, UPDATE, DELETE ON configuration_integration.platform_outbox TO mahjong_workers_rw;
GRANT SELECT, INSERT ON identity_integration.platform_outbox_archive,
    lobby_integration.platform_outbox_archive,
    game_data_integration.platform_outbox_archive TO mahjong_workers_rw;
GRANT SELECT, INSERT ON configuration_integration.platform_outbox_archive TO mahjong_workers_rw;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA worker_integration, worker_projection TO mahjong_workers_rw;
GRANT SELECT ON ALL TABLES IN SCHEMA identity_integration, lobby_integration, worker_integration, worker_projection TO mahjong_monitor_ro;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA admin_monitor FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA admin_monitor FROM PUBLIC;

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
        WHERE schemaname IN (
            'auth', 'session', 'player', 'integration',
            'lobby', 'room', 'matchmaking',
            'player_data', 'admin_monitor',
            'settlement', 'game_record', 'replay', 'leaderboard', 'game_data_integration',
            'configuration', 'configuration_integration',
            'identity_integration', 'lobby_integration', 'worker_integration', 'worker_projection')
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

GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA
    auth, session, player, integration
TO mahjong_auth_rw;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE
    lobby_rooms, active_player_rooms, match_results
TO mahjong_lobby_rw;
GRANT SELECT, INSERT, UPDATE, DELETE
ON TABLE matchmaking.matchmaking_tickets
TO mahjong_lobby_rw;
-- 房间成员、分配和状态历史由安全定义者触发器维护，运行身份只读取审计投影。
GRANT SELECT ON ALL TABLES IN SCHEMA room TO mahjong_lobby_rw;
-- 房间事件只允许追加；玩家历史由安全定义者触发器投影，运行身份仅负责读取调查结果。
GRANT SELECT, INSERT ON TABLE room_event_history TO mahjong_lobby_rw;
GRANT SELECT ON TABLE player_room_history, player_connection_history
TO mahjong_lobby_rw;
REVOKE ALL ON ALL TABLES IN SCHEMA player_data
FROM mahjong_player_data_rw, mahjong_player_data;
GRANT SELECT ON ALL TABLES IN SCHEMA player_data TO mahjong_monitor_ro;
-- 不可变触发器会拒绝权威历史 UPDATE/DELETE；运行身份仍需更新排行榜和 Outbox 调度状态。
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA
    settlement, game_record, replay, leaderboard, game_data_integration
TO mahjong_game_data_rw;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA
    configuration, configuration_integration
TO mahjong_configuration_rw;

GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE
    admin_monitor.action_requests,
    admin_monitor.action_approvals,
    admin_monitor.command_outbox,
    admin_monitor.management_cases,
    admin_monitor.player_asset_operations,
    admin_monitor.player_evidence,
    admin_monitor.player_chat_access_grants,
    admin_monitor.admin_sessions
TO mahjong_admin_rw;
-- 登录安全事件只能追加和查询，禁止 Admin 运行身份覆盖或删除历史失败记录。
GRANT SELECT, INSERT ON TABLE admin_monitor.admin_login_security_events
TO mahjong_admin_rw;
GRANT SELECT, INSERT ON TABLE admin_monitor.audit_ledger
TO mahjong_admin_rw, mahjong_audit_append;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA admin_monitor
TO mahjong_admin_rw, mahjong_audit_append;
GRANT SELECT, UPDATE ON TABLE admin_monitor.audit_archive_outbox
TO mahjong_archive_dispatch;

GRANT SELECT ON TABLE
    lobby_rooms, active_player_rooms, match_results,
    room_event_history, player_room_history, player_connection_history
TO mahjong_monitor_ro;
GRANT SELECT ON ALL TABLES IN SCHEMA auth, session, player, integration
TO mahjong_monitor_ro;
GRANT SELECT ON ALL TABLES IN SCHEMA lobby, room, matchmaking
TO mahjong_monitor_ro;
GRANT SELECT ON ALL TABLES IN SCHEMA player_data, admin_monitor
TO mahjong_monitor_ro;
GRANT SELECT ON ALL TABLES IN SCHEMA settlement, game_record, replay, leaderboard, game_data_integration
TO mahjong_monitor_ro;
GRANT SELECT ON ALL TABLES IN SCHEMA configuration, configuration_integration
TO mahjong_monitor_ro;

ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA configuration
    REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA configuration
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO mahjong_configuration_rw;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA configuration_integration
    REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA configuration_integration
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO mahjong_configuration_rw;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA configuration_integration
    GRANT SELECT, UPDATE, DELETE ON TABLES TO mahjong_workers_rw;

ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA settlement
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO mahjong_game_data_rw;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA game_record
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO mahjong_game_data_rw;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA replay
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO mahjong_game_data_rw;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA leaderboard
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO mahjong_game_data_rw;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA game_data_integration
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO mahjong_game_data_rw;
-- 后续迁移创建的新消息表默认拒绝 PUBLIC；生产者仅追加，Worker 才可维护消息状态。
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA identity_integration
    REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA identity_integration
    GRANT SELECT, INSERT ON TABLES TO mahjong_auth_rw;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA identity_integration
    GRANT SELECT, UPDATE, DELETE ON TABLES TO mahjong_workers_rw;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA lobby_integration
    REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA lobby_integration
    GRANT SELECT, INSERT ON TABLES TO mahjong_lobby_rw;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA lobby_integration
    GRANT SELECT, UPDATE, DELETE ON TABLES TO mahjong_workers_rw;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA worker_integration
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO mahjong_workers_rw;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA worker_projection
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO mahjong_workers_rw;

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

-- 阶段 4 房间投影同样以迁移所有者执行，避免 Lobby 运行账号获得状态历史和分配证据的任意写权限。
ALTER FUNCTION room.project_room_members() OWNER TO mahjong_migration;
ALTER FUNCTION room.project_room_members() SECURITY DEFINER;
ALTER FUNCTION room.project_room_members()
    SET search_path = pg_catalog, public, room;
REVOKE ALL ON FUNCTION room.project_room_members() FROM PUBLIC;
ALTER FUNCTION room.project_room_state_history() OWNER TO mahjong_migration;
ALTER FUNCTION room.project_room_state_history() SECURITY DEFINER;
ALTER FUNCTION room.project_room_state_history()
    SET search_path = pg_catalog, public, room;
REVOKE ALL ON FUNCTION room.project_room_state_history() FROM PUBLIC;
ALTER FUNCTION room.project_room_allocation() OWNER TO mahjong_migration;
ALTER FUNCTION room.project_room_allocation() SECURITY DEFINER;
ALTER FUNCTION room.project_room_allocation()
    SET search_path = pg_catalog, public, room;
REVOKE ALL ON FUNCTION room.project_room_allocation() FROM PUBLIC;

-- 后续由 migration 身份创建的对象默认不向 PUBLIC 泄露，并自动继承对应最小权限。
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA player_data
    REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA player_data
    REVOKE ALL ON TABLES FROM mahjong_player_data_rw;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA admin_monitor
    REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA admin_monitor
    GRANT SELECT ON TABLES TO mahjong_monitor_ro;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA auth
    REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA session
    REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA player
    REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA integration
    REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA lobby
    REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA room
    REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA matchmaking
    REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA room
    GRANT SELECT ON TABLES TO mahjong_lobby_rw, mahjong_monitor_ro;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA matchmaking
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO mahjong_lobby_rw;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA matchmaking
    GRANT SELECT ON TABLES TO mahjong_monitor_ro;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA auth
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO mahjong_auth_rw;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA session
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO mahjong_auth_rw;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA player
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO mahjong_auth_rw;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA integration
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO mahjong_auth_rw;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA auth
    GRANT SELECT ON TABLES TO mahjong_monitor_ro;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA session
    GRANT SELECT ON TABLES TO mahjong_monitor_ro;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA player
    GRANT SELECT ON TABLES TO mahjong_monitor_ro;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA integration
    GRANT SELECT ON TABLES TO mahjong_monitor_ro;

-- 阶段 8.3 Economy 独占资产与奖励表；PlayerData 和 Admin 均不获得这些 Schema 的写权限。
REVOKE ALL ON SCHEMA inventory, reward, economy_integration FROM PUBLIC;
GRANT USAGE ON SCHEMA inventory, reward, economy_integration TO mahjong_economy_rw, mahjong_monitor_ro;
ALTER SCHEMA inventory OWNER TO mahjong_migration;
ALTER SCHEMA reward OWNER TO mahjong_migration;
ALTER SCHEMA economy_integration OWNER TO mahjong_migration;
REVOKE ALL ON ALL TABLES IN SCHEMA inventory, reward, economy_integration FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA inventory, reward, economy_integration TO mahjong_economy_rw;
GRANT SELECT ON ALL TABLES IN SCHEMA inventory, reward, economy_integration TO mahjong_monitor_ro;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA inventory
    GRANT SELECT, INSERT, UPDATE ON TABLES TO mahjong_economy_rw;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA reward
    GRANT SELECT, INSERT, UPDATE ON TABLES TO mahjong_economy_rw;
ALTER DEFAULT PRIVILEGES FOR ROLE mahjong_migration IN SCHEMA economy_integration
    GRANT SELECT, INSERT, UPDATE ON TABLES TO mahjong_economy_rw;
