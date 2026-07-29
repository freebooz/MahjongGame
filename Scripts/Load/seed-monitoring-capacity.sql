\set ON_ERROR_STOP on

-- 仅用于隔离容量环境：生成 10 万个脱敏测试玩家，时间和 ID 顺序可重现。
INSERT INTO auth_identities(
    installation_hash,
    player_id,
    display_name,
    provider,
    created_at_utc,
    updated_at_utc)
SELECT
    repeat(md5('capacity-install-' || value), 2),
    'capacity-player-' || lpad(value::text, 6, '0'),
    'Capacity' || lpad(value::text, 6, '0'),
    'CapacityTest',
    TIMESTAMPTZ '2026-07-29 00:00:00+00'
        - value * INTERVAL '1 second',
    TIMESTAMPTZ '2026-07-29 00:00:00+00'
        - value * INTERVAL '1 second'
FROM generate_series(1, 100000) AS value
ON CONFLICT DO NOTHING;

-- 生成 1 万个房间快照；不包含真实玩家、IP、令牌或支付数据。
INSERT INTO lobby_rooms(
    room_id,
    room_code,
    lifecycle,
    state_sequence,
    payload,
    created_at_utc,
    updated_at_utc)
SELECT
    'capacity-room-' || lpad(value::text, 5, '0'),
    lpad(value::text, 6, '0'),
    CASE WHEN value % 3 = 0 THEN 'Playing' ELSE 'Waiting' END,
    1,
    jsonb_build_object(
        'roomId', 'capacity-room-' || lpad(value::text, 5, '0'),
        'roomCode', lpad(value::text, 6, '0'),
        'ownerPlayerId', 'capacity-player-' || lpad(value::text, 6, '0'),
        'roundCount', 8,
        'publicRoom', true,
        'autoStart', false,
        'maximumPlayers', 4,
        'ruleSnapshot', jsonb_build_object('gameMode', 'Standard'),
        'lifecycle', CASE WHEN value % 3 = 0 THEN 'Playing' ELSE 'Waiting' END,
        'playerIds', jsonb_build_array(),
        'matchId', 'capacity-match-' || lpad(value::text, 5, '0'),
        'stateSequence', 1,
        'createdAtUtc', to_jsonb(
            TIMESTAMPTZ '2026-07-29 00:00:00+00'
                - value * INTERVAL '1 second'),
        'updatedAtUtc', to_jsonb(
            TIMESTAMPTZ '2026-07-29 00:00:00+00'
                - value * INTERVAL '1 second'),
        'newPlayersProhibited', false,
        'maintenanceMode', false,
        'markedAbnormal', false),
    TIMESTAMPTZ '2026-07-29 00:00:00+00'
        - value * INTERVAL '1 second',
    TIMESTAMPTZ '2026-07-29 00:00:00+00'
        - value * INTERVAL '1 second'
FROM generate_series(1, 10000) AS value
ON CONFLICT DO NOTHING;

ANALYZE auth_identities;
ANALYZE lobby_rooms;
