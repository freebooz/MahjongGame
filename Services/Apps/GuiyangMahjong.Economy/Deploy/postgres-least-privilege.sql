-- 由数据库管理员替换角色口令并执行；运行身份无 CREATE、ALTER、DROP 权限。
REVOKE ALL ON SCHEMA inventory, reward, economy_integration FROM PUBLIC;
GRANT USAGE ON SCHEMA inventory, reward, economy_integration TO mahjong_economy_runtime;
GRANT SELECT, INSERT, UPDATE ON inventory.wallet_balances TO mahjong_economy_runtime;
GRANT SELECT, INSERT ON inventory.wallet_transactions TO mahjong_economy_runtime;
GRANT SELECT, INSERT, UPDATE ON reward.reward_grants TO mahjong_economy_runtime;
GRANT SELECT, INSERT, UPDATE ON economy_integration.platform_outbox TO mahjong_economy_runtime;
