# 阶段 4：LobbyControlApp 增量升级实施与验收报告

## 1. 阶段结论

本阶段在不改变 Lobby 部署单元、不删除旧接口、不修改麻将实时规则的前提下，建立了
`Lobby`、`Rooms`、`Matchmaking`、`Reconnection`、`GameRouting`、
`Administration`、`Infrastructure` 七个职责边界。

外部 HTTP 路径和既有请求字段保持兼容。房间控制面增加 `StateVersion`、`RoomEpoch`、
显式座位、状态历史与分配历史；匹配票据以 PostgreSQL 作为权威来源。
Redis 仍只承担缓存、在线投影、限时幂等和监控热数据，不作为房间生命周期或匹配消费的唯一依据。

## 2. 实施前真实状态

- `LobbyService` 通过 partial 文件同时承担创建/加入/关闭、DS 注册/心跳、重连和结算。
- `ILobbyStore` 同时包含生命周期、成员、监控查询和结算持久化。
- 房间状态只有 `Creating/Allocating/Waiting/Playing/Settling/Closed/Failed`。
- PostgreSQL 使用 `state_sequence` 乐观并发，但不存在 `state_version` 与 `room_epoch`。
- 房间成员只存于 JSONB `playerIds`，没有显式座位权威投影。
- 没有匹配票据权威表或原子候选保留能力。
- DS 旧实例主要通过 `server_instance_id` 判断，重新分配竞态缺少代际 fencing token。
- 玩家长期历史和最终结算仍位于 Lobby，是需要后续迁出的 Legacy 职责。

## 3. 本阶段不处理的内容

- 不修改实时麻将规则、手牌或出牌状态。
- 不让 Lobby 直接调用 Kubernetes/Agones；编排调用仍只进入 Allocator。
- 不实现段位、赛事、跨地域匹配或复杂扩圈策略。
- 不迁移玩家资产、奖励、证据和最终结算数据所有权。
- 不删除旧状态字符串、旧 HTTP 路径或 `ILobbyStore` 兼容入口。
- 不引入 NATS 或新的生产消息总线。

## 4. 模块职责与依赖

```text
Api
 ├─ Lobby/LobbyReadService
 ├─ Reconnection/ReconnectionService
 └─ Services/LobbyService（兼容编排入口）
          │
          ├─ Rooms/IRoomReader + IRoomWriter
          ├─ GameRouting/GameRoutingPolicy
          ├─ Matchmaking/IMatchmakingTicketStore
          ├─ Administration（管理命令边界）
          └─ Infrastructure（Legacy 归属声明）
                    │
                    └─ Storage/ILobbyStore（阶段 4 兼容聚合适配器）
```

- `Lobby` 只做公开目录聚合，不写成员、不迁移状态、不调用编排器。
- `Rooms` 定义房间读写端口、显式座位和状态历史模型。
- `Matchmaking` 提供票据创建、原子候选保留和幂等消费。
- `Reconnection` 只根据认证玩家的权威活动房间映射签发当前 Epoch 路由。
- `GameRouting` 统一执行 Epoch fencing 和重新分配代际递增。
- `Administration` 只定义房间控制面命令，不接触结算和玩家资产。
- `Infrastructure` 明确旧职责目标归属，禁止继续扩展 Legacy 写模型。

## 5. 房间状态机

规范状态为：

```text
Created → Waiting → Ready → Allocating → Starting → Playing
                                      ↘ Recovering ↗
Playing → Suspended → Recovering
Playing → Settling → Finished → Archived
任意允许阶段 → Terminating → Finished/Aborted/Archived
异常阶段 → Aborted → Archived
```

所有代码迁移均通过 `RoomStateMachine` 白名单并递增同一个乐观并发版本。
`StateSequence` 是旧接口字段，`StateVersion` 是其规范只读别名；数据库以
`state_version` 作为条件更新列，同时在兼容期保留 `state_sequence`。

旧 JSON 状态 `Creating/Closed/Failed` 可继续读取。外部 JSON 在兼容期继续写出这三个旧值；
数据库与内部审计使用 `Created/Finished/Aborted` 规范名称。

## 6. RoomEpoch 与 DS 路由

`RoomEpoch` 初始为 1，每次重新分配先递增再调用 Allocator。它已贯通：

```text
Lobby 房间快照
  → Allocator AllocationRequest/持久状态
  → 本机 DS -RoomEpoch 参数
  或 Agones mahjong.freebooz/room-epoch Annotation
  → UE DS 注册
  → Lobby 注册回执/Bootstrap
  → DS 心跳
  → GameServerRoute
  → Join Ticket
  → UE Join Ticket 校验
```

初始 Epoch 可通过 `Lobby__Matchmaking__AllowLegacyInitialEpoch=true` 暂时接受未携带 Epoch
的旧 DS；一旦房间发生重新分配，Epoch 必须完全匹配，旧注册、旧心跳和旧 Join Ticket
都不能覆盖或进入新实例。

新增内部幂等入口：

```text
POST /internal/gameservers/reallocate
Idempotency-Key: <稳定命令键>
Authorization: 内部服务凭据
```

命令先持久化 `Recovering + RoomEpoch+1`，再申请新实例；同一幂等键不会重复递增 Epoch。

## 7. 匹配基础能力

`IMatchmakingTicketStore` 提供：

- 创建或返回玩家在队列中的唯一活动票据；
- 按创建顺序原子保留指定数量候选；
- 候选数量不足时整组不保留；
- `ReservationId` 绑定候选组；
- 仅同一 `ReservationId` 可以消费；
- 重复消费返回 Duplicate；
- 过期票据释放玩家+队列唯一约束。

生产实现使用 PostgreSQL `FOR UPDATE SKIP LOCKED` 和部分唯一索引。
内存实现只用于本地开发和单元测试。阶段 4 没有增加复杂匹配公网 API。

## 8. 数据库变化与所有权

### 8.1 兼容列

`public.lobby_rooms` 增加：

- `state_version BIGINT NOT NULL`；
- `room_epoch BIGINT NOT NULL DEFAULT 1`。

旧表名、旧列和 JSONB 快照保留，旧 Lobby 镜像可以忽略新增列。

### 8.2 新逻辑 Schema

```text
room.room_members
room.room_allocations
room.room_state_history
matchmaking.matchmaking_tickets
```

- `room_members`：房间内 PlayerId 唯一，活动 SeatIndex 唯一。
- `room_allocations`：`(room_id, room_epoch)` 唯一，保留每代实例记录。
- `room_state_history`：`(room_id, state_version)` 唯一，只追加状态证据。
- `matchmaking_tickets`：活动玩家+队列部分唯一，票据版本必须为正。

旧 JSONB 数据会按 `playerIds` 顺序回填座位。成员、分配和状态历史通过数据库触发器与房间
写事务原子提交。

### 8.3 最小权限

- `mahjong_lobby_rw` 对旧房间写表和匹配票据具有所需 DML 权限。
- 对 `room` 审计投影只有 SELECT。
- 投影触发器以 `mahjong_migration` 所有者执行并固定 `search_path`。
- Lobby 运行身份没有 `CREATE`/DDL 权限。
- `mahjong_monitor_ro` 对新 Schema 仅有 SELECT。

### 8.4 迁移与回滚

- 升级入口：`Services/GuiyangMahjong.Lobby/Storage/schema.sql`。
- 安全回滚：`Services/GuiyangMahjong.Lobby/Migrations/0004_lobby_control_app.down.sql`。
- 回滚脚本在存在活动 `RoomEpoch > 1` 房间时拒绝执行。
- 回滚删除阶段 4 触发器、函数和新表，但保留 `state_version/room_epoch` 兼容列，避免破坏数据。

## 9. Redis 与消息事件

本阶段没有新增权威 Redis 键。现有键保持：

- `guiyang:lobby:v1:room:*`：可重建房间热缓存；
- `:idempotency:*`：HTTP 命令协调；
- `:presence`：在线投影；
- `:monitor:*`：监控热数据；
- `:events`：实时事件分发。

匹配正确性不依赖 Redis。Redis 队列即使丢失，也可以从 PostgreSQL 的 Pending/Reserved
票据恢复。

没有新增 NATS 事件。继续复用 `room.updated`、`server.assigned`、`room.closed`；
规范状态历史和分配历史在 PostgreSQL 同事务记录。

## 10. 配置变化

新增默认配置：

```json
{
  "Lobby": {
    "Matchmaking": {
      "TicketTtlSeconds": 120,
      "ReconnectionWindowSeconds": 120,
      "AllowLegacyInitialEpoch": true
    }
  }
}
```

环境变量覆盖：

```text
Lobby__Matchmaking__TicketTtlSeconds
Lobby__Matchmaking__ReconnectionWindowSeconds
Lobby__Matchmaking__AllowLegacyInitialEpoch
```

完成 DS 全量升级且超过最长旧实例生命周期后，应将
`AllowLegacyInitialEpoch` 设为 `false`。

## 11. API 兼容策略

- 所有既有公开、内部、管理和监控路径保留。
- 既有请求字段不删除；DS 的 `roomEpoch` 是追加字段，初始兼容值为 0。
- `GameServerRoute`、注册回执和 Bootstrap 追加 Epoch/构建/规则/协议字段。
- 旧终态字符串继续作为外部 JSON 值输出。
- Dedicated Server 原生游戏网络不经过 EdgeGateway。
- 结算接口和结果结构未改变。

## 12. Legacy 迁移计划

| 当前 Lobby 职责 | 阶段 4 状态 | 目标归属 |
|---|---|---|
| 玩家长期房间/连接历史 | 保留 Legacy，只读扩展冻结 | GameData |
| 最终结算 | 保留现有适配器，不新增业务能力 | GameData/Settlement |
| 资产变更 | 不属于 Lobby | Economy |
| 动作与证据日志 | 仅保留现有调查投影 | ReplayEvidence |
| 复杂玩家监控/风险 | 不新增 | TrustSafety |

## 13. 验证结果

已完成：

- `.NET` 全解决方案编译：0 警告、0 错误。
- Lobby 单元/集成基线：60 通过，外部依赖未注入时 8 项条件跳过。
- 全解决方案自动化基线：215 通过，23 项因未注入外部依赖而条件跳过，0 失败。
- Lobby PostgreSQL/Redis 外部集成：8/8 通过。
- Allocator Epoch 传播和旧 Epoch 注册拒绝测试通过。
- 架构测试验证七个模块目录和 Lobby 不引用 Kubernetes/Agones 客户端。
- PostgreSQL 17 升级 SQL 实际执行通过。
- PostgreSQL 17 回滚 SQL 实际执行通过，新表为 0，兼容列保留 2 个。
- 最小权限实际验证：房间/匹配 DML 和安全投影成功，DDL 按预期拒绝。
- Linux Compose 与 Observability Compose 配置展开通过。
- Kubernetes/Agones 清单离线解析 32 个 YAML 对象通过。

未完成：

- 当前 `D:\Program Files\Epic Games\UE_5.8` 缺少
  `Engine/Build/BatchFiles/Build.bat` 与 `RunUAT.bat`，因此无法执行 UBT Target 图、
  Server 编译和 UE 自动化测试。源码、Target 与 Build.cs 已做静态边界检查，但不能替代编译。
- 仓库没有 Helm Chart，本阶段无 Helm 模板可验证。

## 14. 回滚步骤

1. 禁止新房间和 DS 重新分配。
2. 等待活动 `RoomEpoch > 1` 房间结束或显式终止。
3. 回退 Lobby、Allocator 和 Dedicated Server 镜像到阶段 3 兼容版本。
4. 如只需应用回滚，保留数据库新增对象即可，旧镜像会忽略附加列。
5. 如需数据库逻辑回滚，先归档匹配票据与房间历史，再以迁移身份执行
   `0004_lobby_control_app.down.sql`。
6. 验证旧 `/v1/rooms`、加入、路由、重连、DS 注册、心跳和结算链路。

## 15. 阶段验收

`.NET`、数据库、权限、Compose、接口兼容与模块边界满足阶段 4 验收目标。
由于 UE 5.8 本机安装不完整，完整验收状态为“有条件通过”：进入下一阶段前必须恢复完整
UE 5.8 工具链，完成 `GuiyangMahjongServer` 编译和
`GuiyangMahjong.GameServer.*` 自动化测试。

## 16. 阶段 4 实际修改文件

### 16.1 LobbyControl 与契约

- `Services/GuiyangMahjong.Lobby/Api/LobbyEndpoints.Internal.cs`
- `Services/GuiyangMahjong.Lobby/Api/LobbyEndpoints.Public.cs`
- `Services/GuiyangMahjong.Lobby/Administration/RoomAdministrationContracts.cs`
- `Services/GuiyangMahjong.Lobby/Domain/LobbyModels.cs`
- `Services/GuiyangMahjong.Lobby/Domain/RoomLifecycleJsonConverter.cs`
- `Services/GuiyangMahjong.Lobby/Domain/RoomStateMachine.cs`
- `Services/GuiyangMahjong.Lobby/GameRouting/GameRoutingPolicy.cs`
- `Services/GuiyangMahjong.Lobby/Infrastructure/LegacyResponsibilityBoundary.cs`
- `Services/GuiyangMahjong.Lobby/Lobby/LobbyReadService.cs`
- `Services/GuiyangMahjong.Lobby/Matchmaking/MatchmakingContracts.cs`
- `Services/GuiyangMahjong.Lobby/Matchmaking/InMemoryMatchmakingTicketStore.cs`
- `Services/GuiyangMahjong.Lobby/Matchmaking/PostgresMatchmakingTicketStore.cs`
- `Services/GuiyangMahjong.Lobby/Options/LobbyOptions.cs`
- `Services/GuiyangMahjong.Lobby/Program.cs`
- `Services/GuiyangMahjong.Lobby/Reconnection/ControlDeviceLeaseContracts.cs`
- `Services/GuiyangMahjong.Lobby/Reconnection/ReconnectionService.cs`
- `Services/GuiyangMahjong.Lobby/Rooms/RoomModuleContracts.cs`
- `Services/GuiyangMahjong.Lobby/Security/JoinTicketIssuer.cs`
- `Services/GuiyangMahjong.Lobby/Services/AllocatorClient.cs`
- `Services/GuiyangMahjong.Lobby/Services/LobbyService.GameServers.cs`
- `Services/GuiyangMahjong.Lobby/Services/LobbyService.RoomLifecycle.cs`
- `Services/GuiyangMahjong.Lobby/Storage/ILobbyStore.cs`
- `Services/GuiyangMahjong.Lobby/Storage/InMemoryLobbyStore.cs`
- `Services/GuiyangMahjong.Lobby/Storage/RedisPostgresLobbyStore.cs`
- `Services/GuiyangMahjong.Lobby/README.md`
- `Services/GuiyangMahjong.Lobby/appsettings.json`
- `Contracts/OpenAPI/lobby-v1.openapi.yaml`

### 16.2 数据库与权限

- `Services/GuiyangMahjong.Lobby/Storage/schema.sql`
- `Services/GuiyangMahjong.Lobby/Migrations/0004_lobby_control_app.down.sql`
- `Deploy/postgres/least-privilege/002_grants.sql`

### 16.3 Allocator 与 Dedicated Server Epoch 链路

- `Services/GuiyangMahjong.Allocator/Domain/AllocatorModels.cs`
- `Services/GuiyangMahjong.Allocator/Domain/GameServerProcessContracts.cs`
- `Services/GuiyangMahjong.Allocator/Services/AgonesAllocationClient.cs`
- `Services/GuiyangMahjong.Allocator/Services/AllocatorStateStore.cs`
- `Services/GuiyangMahjong.Allocator/Services/GameServerInstanceManager.cs`
- `Services/GuiyangMahjong.Allocator/Services/GameServerProcessLauncher.cs`
- `Source/GuiyangMahjongServer/Private/Server/GuiyangAgonesLifecycleSubsystem.cpp`
- `Source/GuiyangMahjongServer/Private/Server/GuiyangGameServerBridge.cpp`
- `Source/GuiyangMahjongServer/Private/Server/GuiyangServerTicketVerifier.cpp`
- `Source/GuiyangMahjongServer/Public/Server/GuiyangGameServerBridge.h`
- `Source/GuiyangMahjongServer/Public/Server/GuiyangServerTicketVerifier.h`

### 16.4 自动化验证

- `Services/GuiyangMahjong.Lobby.Tests/LobbyControlStage4Tests.cs`
- `Services/GuiyangMahjong.Lobby.Tests/AllocatorIntegrationDomainTests.cs`
- `Services/GuiyangMahjong.Lobby.Tests/ExternalPersistenceIntegrationTests.cs`
- `Services/GuiyangMahjong.Allocator.Tests/GameServerInstanceManagerTests.cs`
- `Services/GuiyangMahjong.Architecture.Tests/ProjectArchitectureTests.cs`
- `Source/GuiyangMahjongEditorTools/Private/Tests/GuiyangManagedGameServerTests.cpp`

本清单不包含工作区中在阶段 4 开始前已经存在的阶段 3 未提交修改；这些修改在本阶段中被保留且未回退。
