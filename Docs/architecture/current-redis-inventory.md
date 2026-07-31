# 当前 Redis 盘点

> 盘点日期：2026-07-31。当前只有 Lobby 生产项目引用 StackExchange.Redis。

默认键前缀为 `guiyang:lobby:v1`，可通过 `Lobby:Persistence:RedisKeyPrefix` 覆盖。以下用 `{prefix}` 表示该值。

## 1. 键清单

| 键/频道模式 | 数据结构 | 用途 | TTL/上限 | 写入者 | 读取者 | 可丢失/恢复 | 权威性与风险 |
|---|---|---|---|---|---|---|---|
| `{prefix}:room:code:{roomCode}` | String(JSON) | 按房间号的热快照 | 24 小时 | Lobby | Lobby | 可丢失；回源 PostgreSQL | 非权威；按 `stateSequence` Lua 防旧值覆盖 |
| `{prefix}:room:id:{roomId}` | String(JSON) | 按 RoomId 的热快照 | 24 小时 | Lobby | Lobby | 可丢失；回源 PostgreSQL | 非权威 |
| `{prefix}:idempotency:result:{scope}` | String(JSON) | POST 响应幂等缓存 | 默认 86400 秒 | Lobby | Lobby | 可丢失；不能完整重建 | 丢失后依靠 PostgreSQL 约束兜底，但可能重新执行非数据库副作用 |
| `{prefix}:idempotency:lock:{scope}` | String(owner UUID) | 首次执行互斥 | 默认 30 秒 | Lobby | Lobby | 可丢失 | 无 fencing token；超时后旧持有者仍可能继续执行 |
| `{prefix}:presence` | Sorted Set | 玩家最后活跃毫秒时间 | 整键无 TTL；读时删除超过默认 90 秒成员 | Lobby/WebSocket | Lobby/Admin 经 API | 可丢失；玩家重连后重建 | 非权威在线快照 |
| `{prefix}:access-revoked-before:{playerId}` | String(epoch ms) | 拒绝签发时间早于水位的访问令牌 | 默认 120 分钟 | Lobby 管理命令 | Lobby 认证中间件 | 可丢失；不能从 PostgreSQL 重建 | 安全关键临时状态；Redis 丢失可能在令牌自然过期前削弱撤销 |
| `{prefix}:events` | Pub/Sub Channel | 多 Lobby 实例广播大厅事件 | 不持久化 | Lobby | Lobby WebSocket Hub | 可丢失；客户端需快照重同步 | 非权威，订阅断开期间事件丢失 |
| `{prefix}:events:sequence` | String/INCR | Pub/Sub 全局递增序号 | 无 TTL | Lobby | Lobby | 可丢失；重建后序号回退 | 只用于实时流顺序，不能作为持久事件位点 |
| `{prefix}:monitor:room:{roomId}:runtime` | String(JSON) | DS 最新运行时遥测 | 6 小时 | Lobby 心跳处理 | Lobby/Admin 经 API | 可丢失；下一次心跳重建 | 非权威实时快照；TTL 暴露陈旧 |
| `{prefix}:monitor:room:{roomId}:events` | List(JSON) | 最近房间事件热缓存 | 最多 500 条、7 天 | Lobby | Lobby/Admin 经 API | 可丢失；回源 `room_event_history` | PostgreSQL 才是权威历史 |
| `{prefix}:monitor:room:{roomId}:event:{eventId}` | String(marker) | Redis 事件去重 | 7 天 | Lobby | Lobby Lua | 可丢失；PostgreSQL event_id 仍幂等 | 与 List 在 Lua 中原子更新 |

## 2. 数据恢复矩阵

| Redis 数据 | PostgreSQL/事件恢复源 | 恢复行为 |
|---|---|---|
| 房间热快照 | `lobby_rooms` | 缓存 miss 时查询并回填 |
| 房间事件列表 | `room_event_history` | 缓存不足/过期时按游标查询并回填 |
| DS runtime | 无持久快照 | 等待下一次 DS 心跳 |
| Presence | 无持久在线权威 | 玩家 WebSocket/请求活动重新写入 |
| 幂等结果 | 仅部分业务可由唯一约束识别 | 无通用重建 |
| 访问撤销水位 | 无 | 只能等待新管理命令或令牌过期 |
| Pub/Sub 事件与序号 | 房间快照/历史仅能恢复部分业务状态 | 客户端重新拉取快照，不能重放完整频道 |

## 3. 锁与正确性

`RedisIdempotencyStore` 使用 `SET NX PX` 获取锁，锁值为随机 owner，释放时通过 Lua 只删除自己的锁。该实现避免误删新持有者的锁，但没有：

- fencing token；
- 自动续租；
- 操作最长时长与锁 TTL 的强约束；
- 将幂等结果与权威数据库事务放入同一提交边界。

因此当操作超过锁 TTL，第二个请求可能获得新锁并与旧请求并发。当前房间创建/加入依赖 PostgreSQL 唯一约束和状态序列降低重复写风险；事件发布、Allocator 调用等外部副作用仍可能重复。Redis 锁不能被视为业务正确性的唯一保证。

## 4. 关键状态与缓存混用

所有键共享同一连接和逻辑数据库/前缀，其中既有普通房间缓存，也有安全关键的访问撤销水位、幂等结果和实时事件序号。虽然命名空间不同，但没有独立 Redis 实例、ACL 用户、淘汰策略或持久化等级隔离。

风险：

1. 内存压力或误清理可能同时影响性能缓存和安全控制；
2. AOF everysec 不能保证最后一秒撤销水位不丢；
3. 若启用易淘汰策略，撤销水位和幂等结果可能被当作普通缓存淘汰；
4. Pub/Sub 与 List/Strings 的容量、延迟和故障模式不同，却没有独立 SLO。

## 5. 当前权威性判断

- 房间、成员、结算、事件历史：PostgreSQL 权威，Redis 使用正确。
- DS 运行态、Presence、实时事件：临时状态，Redis 使用合理。
- 幂等结果：不是业务权威，但承担跨实例首次执行协调，必须由数据库唯一约束和下游幂等共同兜底。
- 访问撤销水位：目前被用于安全决策，且不可恢复，是“被当作安全权威临时状态”的高风险项。

## 6. 本阶段变化

阶段 0 没有增加、删除或修改任何 Redis 键、TTL、序列化格式、Lua 脚本或连接配置。

