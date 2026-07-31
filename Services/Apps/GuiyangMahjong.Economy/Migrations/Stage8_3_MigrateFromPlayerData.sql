-- 阶段 8.3 停写窗口执行：先部署 Economy Schema，再复制历史数据，最后启用 PlayerData 停写门禁。
BEGIN;

INSERT INTO inventory.wallet_balances(player_id, asset_code, balance, version, updated_at_utc)
SELECT player_id, asset_code, balance, version, updated_at_utc FROM player_data.wallet_balances
ON CONFLICT (player_id, asset_code) DO UPDATE SET
  balance=EXCLUDED.balance, version=EXCLUDED.version, updated_at_utc=EXCLUDED.updated_at_utc
WHERE inventory.wallet_balances.balance=EXCLUDED.balance
  AND inventory.wallet_balances.version=EXCLUDED.version;

-- 旧表未保存来源事件；为历史行生成稳定 UUIDv5 等价摘要，并明确标记迁移来源。
INSERT INTO reward.reward_grants(reward_grant_id, source_event_id, source_reference, player_id,
    asset_code, amount, status, trace_id, claimed_at_utc, revoked_at_utc)
SELECT reward_grant_id,
       md5('player-data-reward:' || reward_grant_id)::uuid,
       'legacy:' || reward_grant_id,
       player_id, asset_code, amount, status, 'legacy-migration', claimed_at_utc, revoked_at_utc
FROM player_data.reward_grants
ON CONFLICT (reward_grant_id) DO NOTHING;

INSERT INTO inventory.wallet_transactions(transaction_id, command_id, operation_type, player_id,
    asset_code, amount, balance_after, balance_version, request_data, case_id, requested_by,
    approved_by, reason, ticket_id, trace_id, completed_at_utc)
SELECT transaction_id, command_id, operation_type, player_id, asset_code, amount, balance_after,
    balance_version, request_data, case_id, requested_by, approved_by, reason, ticket_id, trace_id,
    completed_at_utc
FROM player_data.wallet_transactions
ON CONFLICT (command_id) DO NOTHING;

DO $$
BEGIN
  IF (SELECT count(*) FROM player_data.wallet_balances) <> (SELECT count(*) FROM inventory.wallet_balances)
     OR (SELECT count(*) FROM player_data.reward_grants) <> (SELECT count(*) FROM reward.reward_grants)
     OR (SELECT count(*) FROM player_data.wallet_transactions) <> (SELECT count(*) FROM inventory.wallet_transactions)
  THEN RAISE EXCEPTION 'Stage 8.3 row-count validation failed; transaction is rolled back'; END IF;
END $$;
COMMIT;
