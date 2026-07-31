CREATE TABLE IF NOT EXISTS lobby_rooms (
    room_id VARCHAR(80) PRIMARY KEY,
    room_code CHAR(6) NOT NULL UNIQUE,
    lifecycle VARCHAR(24) NOT NULL,
    state_sequence BIGINT NOT NULL,
    payload JSONB NOT NULL,
    created_at_utc TIMESTAMPTZ,
    updated_at_utc TIMESTAMPTZ NOT NULL
);

-- 阶段 4 保留旧表名以支持原镜像回滚，同时增加规范状态版本和 DS 路由 fencing token。
-- state_sequence 是旧 API/快照字段；state_version 是 Room 模块权威并发列，两者在兼容期保持相同值。
ALTER TABLE lobby_rooms
    ADD COLUMN IF NOT EXISTS created_at_utc TIMESTAMPTZ;
ALTER TABLE lobby_rooms
    ADD COLUMN IF NOT EXISTS state_version BIGINT;
ALTER TABLE lobby_rooms
    ADD COLUMN IF NOT EXISTS room_epoch BIGINT;
UPDATE lobby_rooms
SET created_at_utc = COALESCE(
    created_at_utc,
    (payload->>'createdAtUtc')::timestamptz,
    updated_at_utc)
WHERE created_at_utc IS NULL;
UPDATE lobby_rooms
SET state_version = COALESCE(state_version, state_sequence),
    room_epoch = COALESCE(
        room_epoch,
        NULLIF(payload->>'roomEpoch', '')::bigint,
        1)
WHERE state_version IS NULL OR room_epoch IS NULL;
-- 升级旧 JSONB 快照：按既有 PlayerIds 顺序生成稳定座位，避免运行期首次读取出现空座位模型。
UPDATE lobby_rooms AS room_snapshot
SET payload = jsonb_set(
    room_snapshot.payload,
    '{seats}',
    COALESCE(
        (
            SELECT jsonb_agg(
                jsonb_build_object(
                    'playerId', player.value,
                    'seatIndex', player.ordinality - 1,
                    'joinedAtUtc', room_snapshot.created_at_utc)
                ORDER BY player.ordinality)
            FROM jsonb_array_elements_text(
                COALESCE(
                    room_snapshot.payload->'playerIds',
                    '[]'::jsonb))
                WITH ORDINALITY AS player(value, ordinality)
        ),
        '[]'::jsonb),
    true)
WHERE NOT (room_snapshot.payload ? 'seats');
ALTER TABLE lobby_rooms
    ALTER COLUMN created_at_utc SET NOT NULL;
ALTER TABLE lobby_rooms
    ALTER COLUMN state_version SET NOT NULL;
ALTER TABLE lobby_rooms
    ALTER COLUMN room_epoch SET NOT NULL;
ALTER TABLE lobby_rooms
    ALTER COLUMN room_epoch SET DEFAULT 1;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_lobby_rooms_state_version_positive') THEN
        ALTER TABLE lobby_rooms
            ADD CONSTRAINT ck_lobby_rooms_state_version_positive
            CHECK (state_version > 0);
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_lobby_rooms_epoch_positive') THEN
        ALTER TABLE lobby_rooms
            ADD CONSTRAINT ck_lobby_rooms_epoch_positive
            CHECK (room_epoch > 0);
    END IF;
END;
$$;

CREATE INDEX IF NOT EXISTS ix_lobby_rooms_monitoring_cursor_v2
    ON lobby_rooms(created_at_utc DESC, room_id DESC);

CREATE INDEX IF NOT EXISTS ix_lobby_rooms_lifecycle_updated
    ON lobby_rooms(lifecycle, updated_at_utc DESC);
CREATE INDEX IF NOT EXISTS ix_lobby_rooms_epoch
    ON lobby_rooms(room_id, room_epoch DESC);

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

-- 逻辑 Schema 先用于阶段 4 新增对象；旧表在兼容窗口内保留 public 名称，避免破坏旧镜像回滚。
CREATE SCHEMA IF NOT EXISTS lobby;
CREATE SCHEMA IF NOT EXISTS room;
CREATE SCHEMA IF NOT EXISTS matchmaking;
CREATE SCHEMA IF NOT EXISTS integration;

-- 显式成员/座位写模型由房间快照事务维护。PlayerId 在单房间唯一，SeatIndex 在单房间唯一。
CREATE TABLE IF NOT EXISTS room.room_members (
    room_id VARCHAR(80) NOT NULL REFERENCES public.lobby_rooms(room_id) ON DELETE CASCADE,
    player_id VARCHAR(80) NOT NULL,
    seat_index SMALLINT NOT NULL CHECK (seat_index BETWEEN 0 AND 3),
    joined_at_utc TIMESTAMPTZ NOT NULL,
    left_at_utc TIMESTAMPTZ,
    PRIMARY KEY (room_id, player_id)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_room_members_active_seat
    ON room.room_members(room_id, seat_index)
    WHERE left_at_utc IS NULL;

-- 每次分配记录 RoomEpoch，使审计可以证明旧实例为何被拒绝。
CREATE TABLE IF NOT EXISTS room.room_allocations (
    allocation_id UUID PRIMARY KEY,
    room_id VARCHAR(80) NOT NULL REFERENCES public.lobby_rooms(room_id),
    room_epoch BIGINT NOT NULL CHECK (room_epoch > 0),
    server_instance_id VARCHAR(80),
    state VARCHAR(32) NOT NULL,
    build_version VARCHAR(80) NOT NULL,
    reason VARCHAR(500),
    created_at_utc TIMESTAMPTZ NOT NULL,
    updated_at_utc TIMESTAMPTZ NOT NULL,
    UNIQUE (room_id, room_epoch)
);

-- 状态历史只追加；StateVersion 在单房间内唯一，防止并发命令生成两个“下一状态”。
CREATE TABLE IF NOT EXISTS room.room_state_history (
    event_id UUID PRIMARY KEY,
    room_id VARCHAR(80) NOT NULL REFERENCES public.lobby_rooms(room_id),
    from_state VARCHAR(24) NOT NULL,
    to_state VARCHAR(24) NOT NULL,
    state_version BIGINT NOT NULL,
    room_epoch BIGINT NOT NULL,
    reason VARCHAR(500) NOT NULL,
    trace_id VARCHAR(64) NOT NULL,
    occurred_at_utc TIMESTAMPTZ NOT NULL,
    UNIQUE (room_id, state_version)
);

-- 基础匹配票据以 PostgreSQL 为权威；Redis 队列丢失后可从 Pending/Reserved 票据恢复。
CREATE TABLE IF NOT EXISTS matchmaking.matchmaking_tickets (
    ticket_id UUID PRIMARY KEY,
    player_id VARCHAR(80) NOT NULL,
    queue_name VARCHAR(80) NOT NULL,
    state VARCHAR(24) NOT NULL,
    version BIGINT NOT NULL,
    reservation_id UUID,
    created_at_utc TIMESTAMPTZ NOT NULL,
    expires_at_utc TIMESTAMPTZ NOT NULL,
    reserved_at_utc TIMESTAMPTZ,
    consumed_at_utc TIMESTAMPTZ,
    CONSTRAINT ck_matchmaking_ticket_state
        CHECK (state IN ('Pending', 'Reserved', 'Consumed', 'Expired', 'Cancelled')),
    CONSTRAINT ck_matchmaking_ticket_version CHECK (version > 0)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_matchmaking_active_player_queue
    ON matchmaking.matchmaking_tickets(player_id, queue_name)
    WHERE state IN ('Pending', 'Reserved');
CREATE INDEX IF NOT EXISTS ix_matchmaking_queue_candidates
    ON matchmaking.matchmaking_tickets(queue_name, created_at_utc, ticket_id)
    WHERE state = 'Pending';

-- 从兼容 JSONB 快照投影显式成员表。业务事务提交前触发，失败会回滚整个房间命令。
CREATE OR REPLACE FUNCTION room.project_room_members()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE
    seat_value JSONB;
BEGIN
    UPDATE room.room_members
    SET left_at_utc = NEW.updated_at_utc
    WHERE room_id = NEW.room_id
      AND left_at_utc IS NULL
      AND NOT EXISTS (
          SELECT 1
          FROM jsonb_array_elements(
              COALESCE(NEW.payload->'seats', '[]'::jsonb)) AS seat
          WHERE seat->>'playerId' = room_members.player_id);

    FOR seat_value IN
        SELECT value
        FROM jsonb_array_elements(
            COALESCE(NEW.payload->'seats', '[]'::jsonb)) AS item(value)
    LOOP
        INSERT INTO room.room_members(
            room_id, player_id, seat_index, joined_at_utc, left_at_utc)
        VALUES (
            NEW.room_id,
            seat_value->>'playerId',
            (seat_value->>'seatIndex')::smallint,
            COALESCE(
                NULLIF(seat_value->>'joinedAtUtc', '')::timestamptz,
                NEW.created_at_utc),
            NULL)
        ON CONFLICT (room_id, player_id) DO UPDATE
        SET seat_index = EXCLUDED.seat_index,
            left_at_utc = NULL;
    END LOOP;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_project_room_members ON public.lobby_rooms;
CREATE TRIGGER trg_project_room_members
AFTER INSERT OR UPDATE OF payload ON public.lobby_rooms
FOR EACH ROW EXECUTE FUNCTION room.project_room_members();

-- 状态历史使用 RoomId+StateVersion 生成确定性事件标识，重复迁移或幂等重放不会生成两条记录。
CREATE OR REPLACE FUNCTION room.project_room_state_history()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE
    previous_state TEXT :=
        CASE WHEN TG_OP = 'INSERT' THEN NEW.lifecycle ELSE OLD.lifecycle END;
BEGIN
    IF TG_OP = 'INSERT'
       OR OLD.lifecycle IS DISTINCT FROM NEW.lifecycle
       OR OLD.state_version IS DISTINCT FROM NEW.state_version THEN
        INSERT INTO room.room_state_history(
            event_id, room_id, from_state, to_state, state_version,
            room_epoch, reason, trace_id, occurred_at_utc)
        VALUES (
            md5(NEW.room_id || ':' || NEW.state_version::text)::uuid,
            NEW.room_id,
            previous_state,
            NEW.lifecycle,
            NEW.state_version,
            NEW.room_epoch,
            CASE WHEN TG_OP = 'INSERT' THEN 'room-created' ELSE 'room-command' END,
            COALESCE(NULLIF(NEW.payload->>'traceId', ''), 'system-generated'),
            NEW.updated_at_utc)
        ON CONFLICT (room_id, state_version) DO NOTHING;
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_project_room_state_history ON public.lobby_rooms;
CREATE TRIGGER trg_project_room_state_history
AFTER INSERT OR UPDATE OF lifecycle, state_version, room_epoch
ON public.lobby_rooms
FOR EACH ROW EXECUTE FUNCTION room.project_room_state_history();

-- 路由分配历史按 RoomEpoch 幂等更新；旧 Epoch 记录永不删除，供事故调查验证 fencing 行为。
CREATE OR REPLACE FUNCTION room.project_room_allocation()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE
    instance_id TEXT := COALESCE(
        NULLIF(NEW.payload->>'pendingServerInstanceId', ''),
        NULLIF(NEW.payload->'route'->>'serverInstanceId', ''),
        NULLIF(NEW.payload->>'lastServerInstanceId', ''));
    allocation_state TEXT := CASE
        WHEN COALESCE(NEW.payload->'route'->>'serverInstanceId', '') <> '' THEN 'Ready'
        WHEN COALESCE(NEW.payload->>'pendingServerInstanceId', '') <> '' THEN 'Allocating'
        WHEN NEW.lifecycle = 'Recovering' THEN 'Recovering'
        ELSE NEW.lifecycle
    END;
BEGIN
    IF instance_id IS NOT NULL OR NEW.lifecycle = 'Recovering' THEN
        INSERT INTO room.room_allocations(
            allocation_id, room_id, room_epoch, server_instance_id,
            state, build_version, reason, created_at_utc, updated_at_utc)
        VALUES (
            md5(NEW.room_id || ':allocation:' || NEW.room_epoch::text)::uuid,
            NEW.room_id,
            NEW.room_epoch,
            instance_id,
            allocation_state,
            COALESCE(NULLIF(NEW.payload->>'buildVersion', ''), 'unknown'),
            NULL,
            NEW.updated_at_utc,
            NEW.updated_at_utc)
        ON CONFLICT (room_id, room_epoch) DO UPDATE
        SET server_instance_id = COALESCE(
                EXCLUDED.server_instance_id,
                room.room_allocations.server_instance_id),
            state = EXCLUDED.state,
            build_version = EXCLUDED.build_version,
            updated_at_utc = EXCLUDED.updated_at_utc;
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_project_room_allocation ON public.lobby_rooms;
CREATE TRIGGER trg_project_room_allocation
AFTER INSERT OR UPDATE OF payload, lifecycle, room_epoch
ON public.lobby_rooms
FOR EACH ROW EXECUTE FUNCTION room.project_room_allocation();

DELETE FROM active_player_rooms AS active
USING lobby_rooms AS room
WHERE active.room_id = room.room_id
  AND (room.lifecycle NOT IN (
        'Created', 'Creating', 'Waiting', 'Ready', 'Allocating',
        'Starting', 'Playing', 'Suspended', 'Recovering',
        'Settling', 'Terminating')
       OR NOT (room.payload->'playerIds' ? active.player_id));

-- Upgrade existing installations deterministically. If historical data contains
-- duplicate active memberships, the most recently updated room owns the lease.
INSERT INTO active_player_rooms(player_id, room_id, match_id, updated_at_utc)
SELECT DISTINCT ON (player.value)
       player.value, room.room_id, room.payload->>'matchId', room.updated_at_utc
FROM lobby_rooms AS room
CROSS JOIN LATERAL jsonb_array_elements_text(room.payload->'playerIds') AS player(value)
WHERE room.lifecycle IN (
        'Created', 'Creating', 'Waiting', 'Ready', 'Allocating',
        'Starting', 'Playing', 'Suspended', 'Recovering',
        'Settling', 'Terminating')
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
                WHEN NEW.lifecycle IN (
                    'Finished', 'Closed', 'Aborted', 'Failed', 'Archived')
                    THEN NEW.lifecycle
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

-- 阶段 9 使用 Lobby 独占集成 Schema，避免与 Identity 的历史 integration Schema 共享写表。
CREATE SCHEMA IF NOT EXISTS lobby_integration;
CREATE TABLE IF NOT EXISTS lobby_integration.platform_outbox (
    event_id TEXT PRIMARY KEY,
    event_type TEXT NOT NULL,
    schema_version INTEGER NOT NULL CHECK (schema_version > 0),
    aggregate_type TEXT NOT NULL,
    aggregate_id TEXT NOT NULL,
    aggregate_version BIGINT NOT NULL CHECK (aggregate_version >= 0),
    payload_json JSONB NOT NULL,
    occurred_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('Pending','Processing','Published','Failed')),
    attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    next_attempt_at TIMESTAMPTZ NOT NULL,
    lock_owner TEXT NULL,
    lease_expires_at TIMESTAMPTZ NULL,
    published_at TIMESTAMPTZ NULL,
    error_summary TEXT NULL
);
CREATE INDEX IF NOT EXISTS ix_lobby_platform_outbox_dispatch
    ON lobby_integration.platform_outbox(status, next_attempt_at, lease_expires_at);
CREATE TABLE IF NOT EXISTS lobby_integration.platform_outbox_archive
    (LIKE lobby_integration.platform_outbox INCLUDING ALL);

CREATE OR REPLACE FUNCTION lobby_integration.append_platform_event(
    p_event_id TEXT,
    p_event_type TEXT,
    p_aggregate_type TEXT,
    p_aggregate_id TEXT,
    p_aggregate_version BIGINT,
    p_occurred_at TIMESTAMPTZ,
    p_trace_id TEXT,
    p_payload JSONB)
RETURNS VOID LANGUAGE plpgsql AS $$
DECLARE
    safe_trace_id TEXT := CASE
        WHEN p_trace_id ~ '^[a-fA-F0-9]{32}$' THEN lower(p_trace_id)
        ELSE md5(p_event_id || ':trace')
    END;
    correlation_id TEXT := md5(p_event_id || ':correlation');
BEGIN
    INSERT INTO lobby_integration.platform_outbox(
        event_id,event_type,schema_version,aggregate_type,aggregate_id,
        aggregate_version,payload_json,occurred_at,created_at,status,
        attempt_count,next_attempt_at)
    VALUES (
        p_event_id,p_event_type,1,p_aggregate_type,p_aggregate_id,
        p_aggregate_version,
        jsonb_build_object(
            'event_id',p_event_id,
            'event_type',p_event_type,
            'schema_version',1,
            'aggregate_type',p_aggregate_type,
            'aggregate_id',p_aggregate_id,
            'aggregate_version',p_aggregate_version,
            'occurred_at',p_occurred_at,
            'producer','lobby-control',
            'trace_id',safe_trace_id,
            'correlation_id',correlation_id,
            'causation_id',NULL,
            'idempotency_key',NULL,
            'payload',p_payload),
        p_occurred_at,CURRENT_TIMESTAMP,'Pending',0,CURRENT_TIMESTAMP)
    ON CONFLICT (event_id) DO NOTHING;
END;
$$;

CREATE OR REPLACE FUNCTION lobby_integration.enqueue_room_platform_events()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE
    event_key TEXT;
    allocation_id TEXT := md5(NEW.room_id || ':' || NEW.room_epoch::text || ':allocation');
    server_instance TEXT := COALESCE(
        NULLIF(NEW.payload->'route'->>'serverInstanceId',''),
        NULLIF(NEW.payload->>'pendingServerInstanceId',''));
BEGIN
    IF TG_OP = 'INSERT' THEN
        event_key := md5(NEW.room_id || ':' || NEW.state_version::text || ':RoomCreated');
        PERFORM lobby_integration.append_platform_event(
            event_key,'room.created','room',NEW.room_id,NEW.state_version,
            NEW.created_at_utc,NEW.payload->>'traceId',
            jsonb_build_object(
                'room_id',NEW.room_id,
                'room_epoch',NEW.room_epoch,
                'owner_player_id',NEW.payload->>'ownerPlayerId',
                'rule_set_version',COALESCE(NEW.payload->>'ruleSetVersion','legacy-v1'),
                'created_at',NEW.created_at_utc));
        RETURN NEW;
    END IF;

    IF OLD.state_version IS DISTINCT FROM NEW.state_version
       OR OLD.lifecycle IS DISTINCT FROM NEW.lifecycle THEN
        event_key := md5(NEW.room_id || ':' || NEW.state_version::text || ':RoomStateChanged');
        PERFORM lobby_integration.append_platform_event(
            event_key,'room.state_changed','room',NEW.room_id,NEW.state_version,
            NEW.updated_at_utc,NEW.payload->>'traceId',
            jsonb_build_object(
                'room_id',NEW.room_id,
                'room_epoch',NEW.room_epoch,
                'previous_state',OLD.lifecycle,
                'current_state',NEW.lifecycle,
                'state_version',NEW.state_version));
    END IF;

    IF NEW.lifecycle = 'Allocating' AND OLD.lifecycle IS DISTINCT FROM NEW.lifecycle THEN
        event_key := md5(NEW.room_id || ':' || NEW.room_epoch::text || ':AllocationRequested');
        PERFORM lobby_integration.append_platform_event(
            event_key,'allocation.requested','room',NEW.room_id,NEW.state_version,
            NEW.updated_at_utc,NEW.payload->>'traceId',
            jsonb_build_object(
                'allocation_id',allocation_id,
                'room_id',NEW.room_id,
                'room_epoch',NEW.room_epoch,
                'requested_at',NEW.updated_at_utc));
    END IF;

    IF server_instance IS NOT NULL
       AND COALESCE(OLD.payload->>'pendingServerInstanceId','')
           IS DISTINCT FROM COALESCE(NEW.payload->>'pendingServerInstanceId','') THEN
        event_key := md5(NEW.room_id || ':' || NEW.room_epoch::text || ':GameServerAllocated');
        PERFORM lobby_integration.append_platform_event(
            event_key,'game_server.allocated','room',NEW.room_id,NEW.state_version,
            NEW.updated_at_utc,NEW.payload->>'traceId',
            jsonb_build_object(
                'allocation_id',allocation_id,
                'room_id',NEW.room_id,
                'server_instance_id',server_instance,
                'allocated_at',NEW.updated_at_utc));
    END IF;

    IF NEW.payload->'route'->>'serverInstanceId' IS NOT NULL
       AND COALESCE(OLD.payload->'route'->>'serverInstanceId','')
           IS DISTINCT FROM COALESCE(NEW.payload->'route'->>'serverInstanceId','') THEN
        event_key := md5(NEW.room_id || ':' || NEW.room_epoch::text || ':GameServerReady');
        PERFORM lobby_integration.append_platform_event(
            event_key,'game_server.ready','room',NEW.room_id,NEW.state_version,
            NEW.updated_at_utc,NEW.payload->>'traceId',
            jsonb_build_object(
                'server_instance_id',NEW.payload->'route'->>'serverInstanceId',
                'room_id',NEW.room_id,
                'build_version',COALESCE(NEW.payload->>'buildVersion','unknown'),
                'ready_at',NEW.updated_at_utc));
    END IF;

    IF NEW.lifecycle = 'Playing' AND OLD.lifecycle IS DISTINCT FROM NEW.lifecycle THEN
        event_key := md5(NEW.room_id || ':' || NEW.state_version::text || ':MatchStarted');
        PERFORM lobby_integration.append_platform_event(
            event_key,'match.started','match',COALESCE(NEW.payload->>'matchId',NEW.room_id),
            NEW.state_version,NEW.updated_at_utc,NEW.payload->>'traceId',
            jsonb_build_object(
                'match_id',COALESCE(NEW.payload->>'matchId',NEW.room_id),
                'room_id',NEW.room_id,
                'rule_set_version',COALESCE(NEW.payload->>'ruleSetVersion','legacy-v1'),
                'started_at',NEW.updated_at_utc));
    END IF;

    IF NEW.lifecycle = 'Finished' AND OLD.lifecycle IS DISTINCT FROM NEW.lifecycle THEN
        event_key := md5(NEW.room_id || ':' || NEW.state_version::text || ':MatchFinished');
        PERFORM lobby_integration.append_platform_event(
            event_key,'match.finished','match',COALESCE(NEW.payload->>'matchId',NEW.room_id),
            NEW.state_version,NEW.updated_at_utc,NEW.payload->>'traceId',
            jsonb_build_object(
                'match_id',COALESCE(NEW.payload->>'matchId',NEW.room_id),
                'room_id',NEW.room_id,
                'result_digest',md5(NEW.payload::text),
                'finished_at',NEW.updated_at_utc));
    END IF;

    IF NEW.lifecycle IN ('Aborted','Archived')
       AND OLD.lifecycle IS DISTINCT FROM NEW.lifecycle THEN
        event_key := md5(NEW.room_id || ':' || NEW.state_version::text || ':RoomTerminated');
        PERFORM lobby_integration.append_platform_event(
            event_key,'room.terminated','room',NEW.room_id,NEW.state_version,
            NEW.updated_at_utc,NEW.payload->>'traceId',
            jsonb_build_object(
                'room_id',NEW.room_id,
                'room_epoch',NEW.room_epoch,
                'reason_code',NEW.lifecycle,
                'terminated_at',NEW.updated_at_utc));
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_enqueue_room_platform_events ON lobby_rooms;
CREATE TRIGGER trg_enqueue_room_platform_events
AFTER INSERT OR UPDATE OF payload,lifecycle,state_version,room_epoch ON lobby_rooms
FOR EACH ROW EXECUTE FUNCTION lobby_integration.enqueue_room_platform_events();

CREATE OR REPLACE FUNCTION lobby_integration.enqueue_connection_platform_event()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE
    target_state TEXT := NEW.payload->'data'->>'to';
    player_id TEXT := NEW.payload->'data'->>'playerId';
    platform_type TEXT;
    platform_payload JSONB;
BEGIN
    IF NEW.event_type <> 'PlayerConnectionChanged'
       OR target_state NOT IN ('Connected','Disconnected')
       OR COALESCE(player_id,'') = '' THEN
        RETURN NEW;
    END IF;
    platform_type := CASE target_state
        WHEN 'Connected' THEN 'player.connected'
        ELSE 'player.disconnected'
    END;
    platform_payload := CASE target_state
        WHEN 'Connected' THEN jsonb_build_object(
            'room_id',NEW.room_id,
            'player_id',player_id,
            'server_instance_id',COALESCE(
                NEW.payload->'data'->>'serverInstanceId','unknown-server'),
            'connected_at',NEW.occurred_at_utc)
        ELSE jsonb_build_object(
            'room_id',NEW.room_id,
            'player_id',player_id,
            'reason_code',COALESCE(NEW.payload->'data'->>'reason','NetworkLost'),
            'disconnected_at',NEW.occurred_at_utc)
    END;
    PERFORM lobby_integration.append_platform_event(
        replace(NEW.event_id::text,'-',''),platform_type,'room',NEW.room_id,
        NEW.state_sequence,NEW.occurred_at_utc,NEW.trace_id,platform_payload);
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_enqueue_connection_platform_event ON room_event_history;
CREATE TRIGGER trg_enqueue_connection_platform_event
AFTER INSERT ON room_event_history
FOR EACH ROW EXECUTE FUNCTION lobby_integration.enqueue_connection_platform_event();

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
