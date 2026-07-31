-- 阶段8.2只读核验：数量、主键、业务唯一键和字段必须完全一致。
BEGIN TRANSACTION READ ONLY;

SELECT
    (SELECT count(*) FROM player_data.evidence_events WHERE evidence_type='Replay') AS source_count,
    (SELECT count(*) FROM replay.legacy_player_evidence) AS target_count;

SELECT source.event_id, source.source_reference
FROM player_data.evidence_events source
LEFT JOIN replay.legacy_player_evidence target ON target.event_id=source.event_id
WHERE source.evidence_type='Replay'
  AND (target.event_id IS NULL
       OR target.player_id<>source.player_id
       OR target.occurred_at<>source.occurred_at_utc
       OR target.source_reference<>source.source_reference
       OR target.data<>source.data
       OR target.sensitivity<>source.sensitivity);

ROLLBACK;
