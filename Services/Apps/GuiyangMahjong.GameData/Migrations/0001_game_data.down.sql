-- 回滚会永久删除 GameData 历史；仅可在未切流或完成备份和审批后由迁移身份执行。
DROP SCHEMA IF EXISTS leaderboard CASCADE;
DROP SCHEMA IF EXISTS replay CASCADE;
DROP SCHEMA IF EXISTS game_record CASCADE;
DROP SCHEMA IF EXISTS settlement CASCADE;
-- GameData 使用独占集成 Schema，不会影响 Identity 的 integration Schema。
DROP SCHEMA IF EXISTS game_data_integration CASCADE;
