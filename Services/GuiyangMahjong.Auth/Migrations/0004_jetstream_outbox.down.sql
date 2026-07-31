-- 回滚先把尚未确认发布的标准事件转换回旧 identity_outbox，确保 NATS 中断期间保存的事件可继续处理。
INSERT INTO integration.identity_outbox(
    event_id,event_type,schema_version,aggregate_type,aggregate_id,
    occurred_at_utc,trace_id,correlation_id,payload,published_at_utc,
    retry_count,next_retry_at_utc,error_summary)
SELECT event_id::uuid,event_type,schema_version,aggregate_type,aggregate_id,
       occurred_at,payload_json->>'trace_id',payload_json->>'correlation_id',
       payload_json->'payload',NULL,attempt_count,next_attempt_at,error_summary
FROM identity_integration.platform_outbox
WHERE status <> 'Published'
ON CONFLICT (event_id) DO NOTHING;

DROP TABLE IF EXISTS identity_integration.platform_outbox_archive;
DROP TABLE IF EXISTS identity_integration.platform_outbox;
DROP SCHEMA IF EXISTS identity_integration;
