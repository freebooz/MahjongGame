-- 仅在所有工作负载完成身份切换并通过观测窗口后执行。
-- 默认事务会在仍存在依赖时拒绝提交，避免误停仍在使用旧共享身份的服务。
BEGIN;
DO $legacy$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM pg_stat_activity
        WHERE usename = 'mahjong'
          AND pid <> pg_backend_pid())
    THEN
        RAISE EXCEPTION '旧共享身份 mahjong 仍有活动连接，拒绝禁用';
    END IF;

    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mahjong') THEN
        ALTER ROLE mahjong NOLOGIN;
    END IF;
END
$legacy$;
COMMIT;
