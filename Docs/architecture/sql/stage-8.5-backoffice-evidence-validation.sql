-- 返回任何行均表示尚不能关闭PlayerData投影路径。
SELECT source.event_id, source.evidence_type
FROM player_data.evidence_events source
LEFT JOIN admin_monitor.player_evidence target USING(event_id)
WHERE source.evidence_type <> 'Replay'
  AND (target.event_id IS NULL
    OR (source.player_id, source.evidence_type, source.occurred_at_utc, source.source_reference, source.data)
       IS DISTINCT FROM
       (target.player_id, target.evidence_type, target.occurred_at_utc, target.source_reference, target.data));

SELECT status, count(*) FROM player_data.projection_outbox
WHERE status <> 'Completed' GROUP BY status;
