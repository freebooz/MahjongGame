-- 工作流 E：生产数据库身份基线。
-- 角色分为“权限角色（NOLOGIN）”与“工作负载身份（LOGIN）”，密码由 Vault、
-- Kubernetes External Secrets 或等价密钥系统在部署时注入，本文件永不保存密码。

DO $roles$
DECLARE
    role_name text;
BEGIN
    FOREACH role_name IN ARRAY ARRAY[
        'mahjong_auth_rw',
        'mahjong_lobby_rw',
        'mahjong_player_data_rw',
        'mahjong_game_data_rw',
        'mahjong_economy_rw',
        'mahjong_configuration_rw',
        'mahjong_workers_rw',
        'mahjong_admin_rw',
        'mahjong_monitor_ro',
        'mahjong_audit_append',
        'mahjong_archive_dispatch'
    ]
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = role_name) THEN
            EXECUTE format(
                'CREATE ROLE %I NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION',
                role_name);
        END IF;
    END LOOP;

    FOREACH role_name IN ARRAY ARRAY[
        'mahjong_migration',
        'mahjong_auth',
        'mahjong_lobby',
        'mahjong_player_data',
        'mahjong_game_data',
        'mahjong_economy',
        'mahjong_configuration',
        'mahjong_workers',
        'mahjong_admin',
        'mahjong_monitor',
        'mahjong_audit_writer',
        'mahjong_archive'
    ]
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = role_name) THEN
            EXECUTE format(
                'CREATE ROLE %I LOGIN PASSWORD NULL NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT',
                role_name);
        END IF;
    END LOOP;
END
$roles$;

-- NOINHERIT 登录身份必须显式 SET ROLE；连接串通过 Options=-c role=... 固定激活唯一权限角色。
GRANT mahjong_auth_rw TO mahjong_auth;
GRANT mahjong_lobby_rw TO mahjong_lobby;
-- 阶段8.6后保留同名角色仅用于识别历史授权；运行身份禁止登录且不再继承读写角色。
REVOKE mahjong_player_data_rw FROM mahjong_player_data;
ALTER ROLE mahjong_player_data NOLOGIN;
GRANT mahjong_game_data_rw TO mahjong_game_data;
GRANT mahjong_economy_rw TO mahjong_economy;
-- Configuration 运行身份只能显式切换到自身最小权限角色，不继承 migration DDL 权限。
GRANT mahjong_configuration_rw TO mahjong_configuration;
GRANT mahjong_workers_rw TO mahjong_workers;
GRANT mahjong_admin_rw TO mahjong_admin;
GRANT mahjong_monitor_ro TO mahjong_monitor;
GRANT mahjong_audit_append TO mahjong_audit_writer;
GRANT mahjong_archive_dispatch TO mahjong_archive;

COMMENT ON ROLE mahjong_migration IS '仅发布流水线使用的 DDL/对象所有者身份，不得注入应用 Pod';
COMMENT ON ROLE mahjong_auth_rw IS 'Auth 业务表最小读写权限';
COMMENT ON ROLE mahjong_lobby_rw IS 'Lobby 房间业务表最小读写权限';
COMMENT ON ROLE mahjong_player_data_rw IS '阶段8.6退役的PlayerData历史权限角色，不得授予运行身份';
COMMENT ON ROLE mahjong_game_data_rw IS 'GameData 结算、战绩、证据目录、排行榜与 Outbox 最小读写权限';
COMMENT ON ROLE mahjong_workers_rw IS 'BackgroundWorkers 仅处理服务 Outbox、Worker Inbox、投影与人工失败事件';
COMMENT ON ROLE mahjong_admin_rw IS 'Admin 管理工作流读写权限，审计账本仅追加';
COMMENT ON ROLE mahjong_monitor_ro IS '跨域只读监控权限，不具备写入或 DDL';
COMMENT ON ROLE mahjong_audit_append IS '审计账本追加权限，禁止修改和清空历史';
COMMENT ON ROLE mahjong_archive_dispatch IS '不可变归档 Outbox 派发权限';
