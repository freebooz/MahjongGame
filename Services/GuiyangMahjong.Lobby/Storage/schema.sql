CREATE TABLE IF NOT EXISTS lobby_rooms (
    room_id VARCHAR(80) PRIMARY KEY,
    room_code CHAR(6) NOT NULL UNIQUE,
    lifecycle VARCHAR(24) NOT NULL,
    state_sequence BIGINT NOT NULL,
    payload JSONB NOT NULL,
    created_at_utc TIMESTAMPTZ,
    updated_at_utc TIMESTAMPTZ NOT NULL
);

-- 旧数据从不可变快照补齐创建时间；后续写入显式维护该列，供稳定键集分页和容量索引使用。
ALTER TABLE lobby_rooms
    ADD COLUMN IF NOT EXISTS created_at_utc TIMESTAMPTZ;
UPDATE lobby_rooms
SET created_at_utc = COALESCE(
    created_at_utc,
    (payload->>'createdAtUtc')::timestamptz,
    updated_at_utc)
WHERE created_at_utc IS NULL;
ALTER TABLE lobby_rooms
    ALTER COLUMN created_at_utc SET NOT NULL;

CREATE INDEX IF NOT EXISTS ix_lobby_rooms_monitoring_cursor_v2
    ON lobby_rooms(created_at_utc DESC, room_id DESC);

CREATE INDEX IF NOT EXISTS ix_lobby_rooms_lifecycle_updated
    ON lobby_rooms(lifecycle, updated_at_utc DESC);

CREATE INDEX IF NOT EXISTS ix_lobby_rooms_player_ids
    ON lobby_rooms USING GIN ((payload->'playerIds'));

-- A player can own exactly one active-room lease. Every room membership mutation
-- updates this table in the same PostgreSQL transaction as the room snapshot.
CREATE TABLE IF NOT EXISTS active_player_rooms (
    player_id VARCHAR(80) PRIMARY KEY,
    room_id VARCHAR(80) NOT NULL REFERENCES lobby_rooms(room_id) ON DELETE CASCADE,
    match_id VARCHAR(80) NOT NULL,
    updated_at_utc TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_active_player_rooms_room
    ON active_player_rooms(room_id);

DELETE FROM active_player_rooms AS active
USING lobby_rooms AS room
WHERE active.room_id = room.room_id
  AND (room.lifecycle NOT IN ('Allocating', 'Waiting', 'Playing', 'Settling')
       OR NOT (room.payload->'playerIds' ? active.player_id));

-- Upgrade existing installations deterministically. If historical data contains
-- duplicate active memberships, the most recently updated room owns the lease.
INSERT INTO active_player_rooms(player_id, room_id, match_id, updated_at_utc)
SELECT DISTINCT ON (player.value)
       player.value, room.room_id, room.payload->>'matchId', room.updated_at_utc
FROM lobby_rooms AS room
CROSS JOIN LATERAL jsonb_array_elements_text(room.payload->'playerIds') AS player(value)
WHERE room.lifecycle IN ('Allocating', 'Waiting', 'Playing', 'Settling')
  AND COALESCE(room.payload->>'matchId', '') <> ''
ORDER BY player.value, room.updated_at_utc DESC
ON CONFLICT (player_id) DO NOTHING;

CREATE TABLE IF NOT EXISTS match_results (
    match_id VARCHAR(80) NOT NULL,
    result_sequence BIGINT NOT NULL,
    room_id VARCHAR(80) NOT NULL,
    payload JSONB NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (match_id, result_sequence)
);

CREATE INDEX IF NOT EXISTS ix_match_results_room
    ON match_results(room_id, created_at_utc DESC);

-- 房间事件以 PostgreSQL 作为合规保留期内的权威历史；Redis 只保存最近事件热缓存。
-- 应用只允许追加，EventId 全局唯一，使 Dedicated Server 重试不会生成重复证据。
CREATE TABLE IF NOT EXISTS room_event_history (
    event_id UUID PRIMARY KEY,
    room_id VARCHAR(80) NOT NULL REFERENCES lobby_rooms(room_id),
    match_id VARCHAR(80),
    state_sequence BIGINT NOT NULL,
    event_type VARCHAR(80) NOT NULL,
    occurred_at_utc TIMESTAMPTZ NOT NULL,
    trace_id VARCHAR(64) NOT NULL,
    payload JSONB NOT NULL,
    ingested_at_utc TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS ix_room_event_history_room_cursor
    ON room_event_history(room_id, occurred_at_utc DESC, event_id DESC);
CREATE INDEX IF NOT EXISTS ix_room_event_history_match
    ON room_event_history(match_id, occurred_at_utc, event_id)
    WHERE match_id IS NOT NULL;

CREATE OR REPLACE FUNCTION reject_room_event_mutation()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION
        'room_event_history is append-only; use the privileged retention workflow';
END;
$$;

DROP TRIGGER IF EXISTS trg_reject_room_event_update_delete
    ON room_event_history;
CREATE TRIGGER trg_reject_room_event_update_delete
BEFORE UPDATE OR DELETE ON room_event_history
FOR EACH ROW EXECUTE FUNCTION reject_room_event_mutation();

DROP TRIGGER IF EXISTS trg_reject_room_event_truncate
    ON room_event_history;
CREATE TRIGGER trg_reject_room_event_truncate
BEFORE TRUNCATE ON room_event_history
FOR EACH STATEMENT EXECUTE FUNCTION reject_room_event_mutation();

-- 玩家房间历史由房间快照事务触发维护，不再从“当前房间”反推历史。
CREATE TABLE IF NOT EXISTS player_room_history (
    player_id VARCHAR(80) NOT NULL,
    room_id VARCHAR(80) NOT NULL REFERENCES lobby_rooms(room_id),
    match_id VARCHAR(80) NOT NULL,
    joined_at_utc TIMESTAMPTZ NOT NULL,
    left_at_utc TIMESTAMPTZ,
    leave_reason VARCHAR(80),
    PRIMARY KEY (player_id, room_id, joined_at_utc)
);

CREATE INDEX IF NOT EXISTS ix_player_room_history_player_cursor
    ON player_room_history(player_id, joined_at_utc DESC, room_id DESC);

-- 连接历史直接由不可变房间事件投影，EventId 同时承担幂等键和证据关联键。
CREATE TABLE IF NOT EXISTS player_connection_history (
    event_id UUID PRIMARY KEY REFERENCES room_event_history(event_id),
    player_id VARCHAR(80) NOT NULL,
    room_id VARCHAR(80) NOT NULL,
    match_id VARCHAR(80),
    from_state VARCHAR(32),
    to_state VARCHAR(32) NOT NULL,
    trustee BOOLEAN,
    occurred_at_utc TIMESTAMPTZ NOT NULL,
    trace_id VARCHAR(64) NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_player_connection_history_player_cursor
    ON player_connection_history(player_id, occurred_at_utc DESC, event_id DESC);

CREATE OR REPLACE FUNCTION project_player_room_history()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE
    player_value TEXT;
    old_players JSONB := COALESCE(OLD.payload->'playerIds', '[]'::jsonb);
    new_players JSONB := COALESCE(NEW.payload->'playerIds', '[]'::jsonb);
    observed_at TIMESTAMPTZ := NEW.updated_at_utc;
BEGIN
    IF TG_OP = 'INSERT' THEN
        old_players := '[]'::jsonb;
        observed_at := NEW.created_at_utc;
    END IF;

    FOR player_value IN
        SELECT value FROM jsonb_array_elements_text(new_players)
        EXCEPT
        SELECT value FROM jsonb_array_elements_text(old_players)
    LOOP
        INSERT INTO player_room_history(
            player_id, room_id, match_id, joined_at_utc)
        VALUES (
            player_value,
            NEW.room_id,
            COALESCE(NULLIF(NEW.payload->>'matchId', ''), NEW.room_id),
            observed_at)
        ON CONFLICT DO NOTHING;
    END LOOP;

    FOR player_value IN
        SELECT value FROM jsonb_array_elements_text(old_players)
        EXCEPT
        SELECT value FROM jsonb_array_elements_text(new_players)
    LOOP
        UPDATE player_room_history
        SET left_at_utc = observed_at,
            leave_reason = CASE
                WHEN NEW.lifecycle IN ('Closed', 'Failed') THEN NEW.lifecycle
                ELSE 'LeftRoom'
            END
        WHERE player_id = player_value
          AND room_id = NEW.room_id
          AND left_at_utc IS NULL;
    END LOOP;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_project_player_room_history ON lobby_rooms;
CREATE TRIGGER trg_project_player_room_history
AFTER INSERT OR UPDATE OF payload, lifecycle ON lobby_rooms
FOR EACH ROW EXECUTE FUNCTION project_player_room_history();

CREATE OR REPLACE FUNCTION project_player_connection_history()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.event_type = 'PlayerConnectionChanged'
       AND COALESCE(NEW.payload->'data'->>'playerId', '') <> ''
       AND COALESCE(NEW.payload->'data'->>'to', '') <> '' THEN
        INSERT INTO player_connection_history(
            event_id, player_id, room_id, match_id,
            from_state, to_state, trustee, occurred_at_utc, trace_id)
        VALUES (
            NEW.event_id,
            NEW.payload->'data'->>'playerId',
            NEW.room_id,
            NEW.match_id,
            NEW.payload->'data'->>'from',
            NEW.payload->'data'->>'to',
            CASE
                WHEN NEW.payload->'data' ? 'trustee'
                    THEN (NEW.payload->'data'->>'trustee')::boolean
                ELSE NULL
            END,
            NEW.occurred_at_utc,
            NEW.trace_id)
        ON CONFLICT DO NOTHING;
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_project_player_connection_history
    ON room_event_history;
CREATE TRIGGER trg_project_player_connection_history
AFTER INSERT ON room_event_history
FOR EACH ROW EXECUTE FUNCTION project_player_connection_history();

-- 为升级前仍在房间中的玩家补齐可查询起点；精确离开时间只从本迁移启用后保证。
INSERT INTO player_room_history(
    player_id, room_id, match_id, joined_at_utc)
SELECT player.value,
       room.room_id,
       COALESCE(NULLIF(room.payload->>'matchId', ''), room.room_id),
       room.created_at_utc
FROM lobby_rooms AS room
CROSS JOIN LATERAL jsonb_array_elements_text(
    COALESCE(room.payload->'playerIds', '[]'::jsonb)) AS player(value)
ON CONFLICT DO NOTHING;
