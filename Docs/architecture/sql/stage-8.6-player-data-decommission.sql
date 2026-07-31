-- 必须先执行 validation，且结果为零行。本脚本只冻结历史数据，不物理删除审计证据。
BEGIN;
REVOKE mahjong_player_data_rw FROM mahjong_player_data;
ALTER ROLE mahjong_player_data NOLOGIN;
REVOKE ALL ON SCHEMA player_data FROM mahjong_player_data_rw, mahjong_player_data;
REVOKE ALL ON ALL TABLES IN SCHEMA player_data FROM mahjong_player_data_rw, mahjong_player_data;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA player_data FROM mahjong_player_data_rw, mahjong_player_data;
GRANT USAGE ON SCHEMA player_data TO mahjong_monitor_ro;
GRANT SELECT ON ALL TABLES IN SCHEMA player_data TO mahjong_monitor_ro;
COMMENT ON SCHEMA player_data IS
    '阶段8.6冻结的PlayerData历史Schema；禁止业务写入，物理删除须经过保留期和独立审批';
COMMIT;
