-- 紧急回滚仅恢复技术访问能力；登录密码必须由密钥系统重新注入，禁止写入本脚本。
BEGIN;
ALTER ROLE mahjong_player_data LOGIN;
GRANT mahjong_player_data_rw TO mahjong_player_data;
GRANT USAGE ON SCHEMA player_data TO mahjong_player_data_rw;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA player_data TO mahjong_player_data_rw;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA player_data TO mahjong_player_data_rw;
COMMENT ON SCHEMA player_data IS 'PlayerData紧急回滚期间临时恢复；完成故障处置后必须重新执行阶段8.6冻结';
COMMIT;
