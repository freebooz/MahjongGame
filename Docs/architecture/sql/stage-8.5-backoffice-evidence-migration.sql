-- 停止PlayerData证据写入和Dispatcher后执行；Admin摄取端点与本脚本共享相同幂等主键。
BEGIN;
INSERT INTO admin_monitor.player_evidence(event_id, player_id, evidence_type, occurred_at_utc,
    source_reference, data, sensitivity, ingested_at_utc)
SELECT event_id, player_id, evidence_type, occurred_at_utc, source_reference, data,
       CASE WHEN evidence_type IN ('AssetChange', 'RewardClaim', 'PaymentOrder') THEN 'Financial'
            ELSE sensitivity END,
       recorded_at_utc
FROM player_data.evidence_events
WHERE evidence_type <> 'Replay'
ON CONFLICT (event_id) DO NOTHING;

DO $$
BEGIN
  IF EXISTS (
    SELECT 1 FROM player_data.evidence_events source
    LEFT JOIN admin_monitor.player_evidence target USING(event_id)
    WHERE source.evidence_type <> 'Replay'
      AND (target.event_id IS NULL
        OR (source.player_id, source.evidence_type, source.occurred_at_utc, source.source_reference, source.data)
           IS DISTINCT FROM
           (target.player_id, target.evidence_type, target.occurred_at_utc, target.source_reference, target.data)))
  THEN RAISE EXCEPTION 'Stage 8.5 evidence reconciliation failed; transaction rolled back'; END IF;
END $$;
COMMIT;
