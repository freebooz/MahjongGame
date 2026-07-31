-- 阶段9回滚先停止产生新平台事件，但保留 Outbox 和归档表，使旧 Worker 或独立排空工具可以处理已提交消息。
DROP TRIGGER IF EXISTS trg_enqueue_connection_platform_event ON room_event_history;
DROP TRIGGER IF EXISTS trg_enqueue_room_platform_events ON lobby_rooms;
DROP FUNCTION IF EXISTS lobby_integration.enqueue_connection_platform_event();
DROP FUNCTION IF EXISTS lobby_integration.enqueue_room_platform_events();
DROP FUNCTION IF EXISTS lobby_integration.append_platform_event(
    TEXT,TEXT,TEXT,TEXT,BIGINT,TIMESTAMPTZ,TEXT,JSONB);

-- 仅禁用生产入口，不删除已提交事件；确认 Pending/Processing 为零并完成审计审批后，后续治理迁移才可删除表。

