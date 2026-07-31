-- 仅由部署作业调用；所有变量必须来自密钥管理系统，禁止写入仓库或命令日志。
\set ON_ERROR_STOP on
ALTER ROLE mahjong_migration PASSWORD :'migration_password';
ALTER ROLE mahjong_auth PASSWORD :'auth_password';
ALTER ROLE mahjong_lobby PASSWORD :'lobby_password';
ALTER ROLE mahjong_game_data PASSWORD :'game_data_password';
ALTER ROLE mahjong_economy PASSWORD :'economy_password';
ALTER ROLE mahjong_configuration PASSWORD :'configuration_password';
ALTER ROLE mahjong_workers PASSWORD :'workers_password';
ALTER ROLE mahjong_admin PASSWORD :'admin_password';
ALTER ROLE mahjong_monitor PASSWORD :'monitor_password';
ALTER ROLE mahjong_audit_writer PASSWORD :'audit_password';
ALTER ROLE mahjong_archive PASSWORD :'archive_password';
