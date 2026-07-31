-- 阶段 8.1 只读验收脚本：确认玩家资料和在线会话已经由 Identity 独占。
-- 本步骤没有源数据迁移；事务固定为只读并最终回滚，允许在验收环境重复执行。
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;

DO $$
BEGIN
    -- 目标表必须真实存在，否则“PlayerData 不含资料表”不能证明所有权迁移完成。
    IF to_regclass('player.player_profiles') IS NULL THEN
        RAISE EXCEPTION 'Identity player.player_profiles is missing';
    END IF;
    IF to_regclass('session.auth_refresh_sessions') IS NULL THEN
        RAISE EXCEPTION 'Identity session.auth_refresh_sessions is missing';
    END IF;

    -- PlayerData 不允许保留或重新建立资料、会话权威表。
    IF EXISTS (
        SELECT 1
        FROM information_schema.tables
        WHERE table_schema = 'player_data'
          AND table_name IN (
              'player_profiles',
              'player_profile',
              'sessions',
              'player_sessions',
              'auth_refresh_sessions')) THEN
        RAISE EXCEPTION 'PlayerData still owns a profile or session table';
    END IF;

    -- 每个身份都应有对应的长期资料；发现缺口时必须先修复 Identity 数据，不能回写 PlayerData。
    IF EXISTS (
        SELECT 1
        FROM auth.auth_identities AS identity
        LEFT JOIN player.player_profiles AS profile
          ON profile.player_id = identity.player_id
        WHERE profile.player_id IS NULL) THEN
        RAISE EXCEPTION 'Identity contains players without player profiles';
    END IF;
END $$;

-- 返回验收快照，便于把数量记录到变更工单；两项数量应相等。
SELECT
    (SELECT COUNT(*) FROM auth.auth_identities) AS identity_count,
    (SELECT COUNT(*) FROM player.player_profiles) AS profile_count;

-- 本脚本无写操作，回滚既是结束动作也是明确的回滚策略。
ROLLBACK;
