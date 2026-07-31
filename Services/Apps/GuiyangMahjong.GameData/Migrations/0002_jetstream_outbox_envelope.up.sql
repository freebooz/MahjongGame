-- 把阶段 7 仅保存 payload 的未发布行升级为完整 EventEnvelope；重复执行不会再次包装。
UPDATE game_data_integration.platform_outbox
SET payload_json = jsonb_build_object(
        'event_id', event_id,
        'event_type', event_type,
        'schema_version', schema_version,
        'aggregate_type', aggregate_type,
        'aggregate_id', aggregate_id,
        'aggregate_version', aggregate_version,
        'occurred_at', occurred_at,
        'producer', 'game-data',
        'trace_id', md5(event_id || ':trace'),
        'correlation_id', md5(event_id || ':correlation'),
        'causation_id', NULL,
        'idempotency_key', NULL,
        'payload', payload_json)
WHERE NOT (payload_json ? 'event_id');

CREATE TABLE IF NOT EXISTS game_data_integration.platform_outbox_archive
    (LIKE game_data_integration.platform_outbox INCLUDING ALL);
