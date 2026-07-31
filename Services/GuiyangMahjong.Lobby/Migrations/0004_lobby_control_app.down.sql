-- 阶段 4 安全回滚脚本。
-- 执行前必须先回退 Lobby/Allocator/DS 镜像并停止新的房间创建；否则新 Epoch 房间可能失去 fencing 保护。
DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM public.lobby_rooms
        WHERE room_epoch > 1
          AND lifecycle NOT IN (
              'Finished', 'Closed', 'Aborted', 'Failed', 'Archived')) THEN
        RAISE EXCEPTION
            '存在仍活动的 RoomEpoch>1 房间，禁止回滚 LobbyControl 数据对象';
    END IF;
END;
$$;

DROP TRIGGER IF EXISTS trg_project_room_allocation
    ON public.lobby_rooms;
DROP TRIGGER IF EXISTS trg_project_room_state_history
    ON public.lobby_rooms;
DROP TRIGGER IF EXISTS trg_project_room_members
    ON public.lobby_rooms;

DROP FUNCTION IF EXISTS room.project_room_allocation();
DROP FUNCTION IF EXISTS room.project_room_state_history();
DROP FUNCTION IF EXISTS room.project_room_members();

-- 匹配票据和房间审计投影在删除前必须由迁移流程导出归档；本脚本不负责导出业务证据。
DROP TABLE IF EXISTS matchmaking.matchmaking_tickets;
DROP TABLE IF EXISTS room.room_state_history;
DROP TABLE IF EXISTS room.room_allocations;
DROP TABLE IF EXISTS room.room_members;

DROP SCHEMA IF EXISTS matchmaking;
DROP SCHEMA IF EXISTS room;
DROP SCHEMA IF EXISTS lobby;

-- state_version/room_epoch 保留在 public.lobby_rooms。
-- 旧 Lobby 镜像会忽略附加列；保留它们可避免破坏性数据丢失，并支持重新升级。
