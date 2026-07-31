-- 阶段8.6退役前门禁：任何返回行都表示不能停止PlayerData或冻结旧Schema。
SELECT 'projection_outbox_not_drained' AS violation, count(*) AS affected
FROM player_data.projection_outbox
WHERE status <> 'Completed'
HAVING count(*) > 0;

SELECT 'wallet_balance_mismatch' AS violation, count(*) AS affected
FROM player_data.wallet_balances source
FULL JOIN inventory.wallet_balances target USING (player_id, asset_code)
WHERE source.player_id IS NULL OR target.player_id IS NULL
   OR source.balance <> target.balance OR source.version <> target.version
HAVING count(*) > 0;

SELECT 'reward_grant_missing' AS violation, count(*) AS affected
FROM player_data.reward_grants source
WHERE NOT EXISTS (
    SELECT 1 FROM reward.reward_grants target
    WHERE target.source_event_id = md5('player-data-reward:' || source.reward_grant_id)::uuid)
HAVING count(*) > 0;

SELECT 'wallet_transaction_missing' AS violation, count(*) AS affected
FROM player_data.wallet_transactions source
WHERE NOT EXISTS (
    SELECT 1 FROM inventory.wallet_transactions target
    WHERE target.command_id = source.command_id)
HAVING count(*) > 0;

SELECT 'replay_evidence_missing' AS violation, count(*) AS affected
FROM player_data.evidence_events source
WHERE source.evidence_type = 'Replay' AND NOT EXISTS (
    SELECT 1 FROM replay.legacy_player_evidence target WHERE target.event_id = source.event_id)
HAVING count(*) > 0;

SELECT 'backoffice_evidence_missing' AS violation, count(*) AS affected
FROM player_data.evidence_events source
WHERE source.evidence_type <> 'Replay' AND NOT EXISTS (
    SELECT 1 FROM admin_monitor.player_evidence target WHERE target.event_id = source.event_id)
HAVING count(*) > 0;
