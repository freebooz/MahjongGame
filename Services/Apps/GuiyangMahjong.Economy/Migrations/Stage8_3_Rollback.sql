-- 回滚只恢复旧写能力；不得删除 Economy 数据，避免丢失切换后已产生的交易。
BEGIN;
DROP TRIGGER IF EXISTS trg_reject_legacy_wallet_balance_write ON player_data.wallet_balances;
DROP TRIGGER IF EXISTS trg_reject_legacy_reward_write ON player_data.reward_grants;
DROP TRIGGER IF EXISTS trg_reject_legacy_wallet_transaction_write ON player_data.wallet_transactions;
COMMIT;
