-- 回退到阶段 7 读取格式；只剥离标准信封，不改变发布状态和重试计数。
UPDATE game_data_integration.platform_outbox
SET payload_json = payload_json->'payload'
WHERE payload_json ? 'event_id';

DROP TABLE IF EXISTS game_data_integration.platform_outbox_archive;
