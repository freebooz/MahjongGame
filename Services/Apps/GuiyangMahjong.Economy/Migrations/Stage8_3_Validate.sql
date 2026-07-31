-- 数量、余额和交易回执必须完全一致；任一查询返回行即不得切换流量。
SELECT 'wallet_count' AS check_name WHERE
 (SELECT count(*) FROM player_data.wallet_balances) <> (SELECT count(*) FROM inventory.wallet_balances);
SELECT 'reward_count' AS check_name WHERE
 (SELECT count(*) FROM player_data.reward_grants) <> (SELECT count(*) FROM reward.reward_grants);
SELECT 'transaction_count' AS check_name WHERE
 (SELECT count(*) FROM player_data.wallet_transactions) <> (SELECT count(*) FROM inventory.wallet_transactions);
SELECT p.player_id, p.asset_code FROM player_data.wallet_balances p
FULL JOIN inventory.wallet_balances e USING(player_id, asset_code)
WHERE (p.balance, p.version) IS DISTINCT FROM (e.balance, e.version);
SELECT p.command_id FROM player_data.wallet_transactions p
FULL JOIN inventory.wallet_transactions e USING(command_id)
WHERE (p.transaction_id, p.balance_after, p.balance_version) IS DISTINCT FROM
      (e.transaction_id, e.balance_after, e.balance_version);
