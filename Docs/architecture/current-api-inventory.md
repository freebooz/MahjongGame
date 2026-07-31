# 当前 API 与通信接口盘点

> 盘点日期：2026-07-31。路径和类型来自实际 Minimal API 映射与 UE 调用代码。  
> 说明：`Bearer(player)` 为 Auth 签发的玩家访问令牌；`Bearer(service/monitor/manage)` 分别代表独立服务、只读监控和管理凭据。所有写接口均不存在“客户端提交权威牌局结果”的入口。

## 1. Auth HTTP API

| 方法与路径 | 请求 → 响应 | 认证/调用方 | 数据写入 | 幂等策略 | 当前风险 |
|---|---|---|---|---|---|
| GET `/health/live` | 无 → live DTO | 无；探针 | 无 | 只读 | 仅进程存活 |
| GET `/health/ready` | 无 → identityStore 状态 | 无；探针 | 无 | 只读 | InMemory 模式不能证明跨实例 |
| GET `/openapi/v1.yaml` | 无 → YAML | 无；开发/契约工具 | 无 | 只读 | 需持续做实现漂移检查 |
| POST `/v1/auth/guest` | `GuestLoginRequest` → `AuthSessionResponse` | 无；UE Client，IP 限流 | identity、refresh session、login event | installation hash/唯一约束；会话 ID 唯一 | 不是显式 Idempotency-Key；网络不确定时可能新建额外刷新会话 |
| POST `/v1/auth/refresh` | `RefreshSessionRequest` → `AuthSessionResponse` | refresh token；UE Client，限流 | 原会话撤销、新会话创建 | 事务内单次轮换；旧 token 不可复用 | POST 不可透明重试；响应丢失会使客户端失去新 token |
| POST `/v1/auth/logout` | `LogoutRequest` → 204 | refresh token；UE Client，限流 | refresh session 撤销 | 已撤销/无效 token 静默完成 | 只撤销指定 refresh session |
| POST `/internal/admin/players/{playerId}/sessions/revoke` | `AdminRevokePlayerSessionsRequest` → `AdminRevokePlayerSessionsResult` | Bearer(manage)；Admin | sessions、`auth_admin_commands` | `Idempotency-Key`/command_id | 跨 Lobby 断连由 Admin 编排，可能部分成功 |
| POST `/internal/admin/players/{playerId}/controls` | `AdminUpdatePlayerControlRequest` → control result | Bearer(manage)；Admin | control、control event、可能撤销 sessions | command_id + expected version | 高风险命令依赖 Admin 双人审批正确传播 |
| GET `/internal/monitoring/players` | 查询/游标 → player page | Bearer(monitor)；Admin | 无 | 只读键集分页 | 搜索与时间游标需容量门禁 |
| GET `/internal/monitoring/players/{playerId}` | path → `PlayerDirectoryDetail`/404 | Bearer(monitor)；Admin | 无 | 只读 | 聚合包含设备/IP 派生信息，必须继续脱敏 |

## 2. Lobby HTTP/WebSocket API

### 2.1 玩家公开接口

`/v1` 全部分支由 `PlayerAuthenticationMiddleware` 验证玩家 Bearer、签发时间和 Redis 撤销水位。

| 方法与路径 | 请求 → 响应 | 调用方 | 数据写入 | 幂等策略 | 当前风险 |
|---|---|---|---|---|---|
| GET `/openapi/v1.yaml` | 无 → YAML | 契约工具 | 无 | 只读 | 工作树中的契约已有未提交修改，冻结前需归档 |
| GET `/v1/lobby/bootstrap` | 无 → `LobbyBootstrapResponse` | UE Client | presence 活跃时间 | 只读业务结果 | Presence 丢失时在线数短暂偏低 |
| GET `/v1/rooms` | 无 → `RoomDirectoryItem[]` | UE Client | 无 | 只读 | 当前返回集合而非公开分页 |
| POST `/v1/rooms` | `CreateRoomRequest` → `RoomOperation`，202 | UE Client | `lobby_rooms`、`active_player_rooms`，Allocator 分配 | 必需 `Idempotency-Key`；Redis 结果 + PostgreSQL 唯一约束 | Redis 锁无 fencing；分配成功但房间绑定失败需回收 |
| POST `/v1/rooms/current/close` | 无 → `RoomOperation` | 房主 UE Client | 房间终态、活动租约；异步 drain | 玩家 + key 幂等；状态序列 | `OnCompleted` 回收失败只留日志/后续超时回收 |
| POST `/v1/rooms/{roomCode}/join` | `JoinRoomRequest` → `GameServerRoute` 或 `RoomOperation` | UE Client | 房间成员/活动租约 | 玩家 + 房间号 + key；数据库租约 | Redis 结果丢失后可重新执行事件副作用 |
| GET `/v1/rooms/{roomCode}/route` | path → `GameServerRoute` | 房间成员 UE Client | 新签发 Join Ticket（无数据库写） | 每次新票，不幂等 | GET 产生安全凭据；代理缓存必须禁用 |
| POST `/v1/reconnect/route` | `ReconnectRouteRequest` → `GameServerRoute` | UE Client | 新签发 Join Ticket | 要求 key 但没有结果存储 | key 只做格式门禁，实际可签发多票 |
| GET `/v1/events` | WebSocket → `LobbyEventEnvelope` 流 | UE Client | Presence/连接集合 | sequence + 客户端重同步 | Redis Pub/Sub 非持久，断档需快照恢复 |

### 2.2 Dedicated Server 与内部控制接口

| 方法与路径 | 请求 → 响应 | 认证/调用方 | 数据写入 | 幂等策略 | 当前风险 |
|---|---|---|---|---|---|
| POST `/internal/gameservers/register` | `GameServerRegistration` → `GameServerRegistrationAck` | DS；一次性 registration credential 在 Allocator 确认 | 房间 route、result credential hash；Allocator 实例 | Allocator registration credential 单次使用；房间状态约束 | Lobby 入口自身不验 Bearer，完全依赖 body 中实例凭据和 Allocator 在线确认 |
| POST `/internal/gameservers/{id}/heartbeat` | `GameServerHeartbeat` → 204 | DS；实例 heartbeat credential 由 Allocator 验证 | Allocator 实例状态、房间 lifecycle、runtime、事件历史 | 实例/房间范围、事件 EventId 去重、状态序列 | 高频跨服务同步调用；网络失败由 DS 下次心跳覆盖 |
| POST `/internal/gameservers/failure` | `GameServerFailure` → 204 | Bearer(service)；Allocator | 房间 Failed | 已终态/不匹配实例忽略 | 通知失败依赖 Allocator 持久重试 |
| POST `/internal/matches/{matchId}/result` | `MatchResultReport` → `MatchResultAck` | DS result credential + `Idempotency-Key` | `match_results`、房间 Closed、runtime | `(match_id,result_sequence)`；同载荷 duplicate，异载荷 conflict | Lobby 同时承担 Settlement；drain 失败返回 503 但结果已持久化 |
| POST `/internal/matches/{matchId}/result/recovery` | `MatchResultReport` → `MatchResultAck` | Bearer(service)；Allocator Outbox recovery | 同上 | 同一结果主键与 payload 比较 | 受信恢复绕过实例 result credential，必须严格保护 service token |
| POST `/internal/admin/players/{playerId}/disconnect` | `AdminDisconnectPlayerRequest` → result | Bearer(manage)；Admin | Redis 撤销水位、Presence、WebSocket 连接 | `Idempotency-Key` + Redis result | 撤销水位不可从 PostgreSQL 恢复 |
| POST `/internal/admin/rooms/{roomId}/controls` | `AdminUpdateRoomControlRequest` → control result | Bearer(manage)；Admin | 房间状态、事件历史、可能 drain | `Idempotency-Key` + expected state sequence | 只允许标异常、禁加入、维护、强解散；不允许改结果 |

### 2.3 Lobby 监控接口

全部使用 Bearer(monitor)，调用方为 Admin。

| 方法与路径 | 请求 → 响应 | 数据源/写入 | 幂等与风险 |
|---|---|---|---|
| GET `/internal/monitoring/rooms` | 过滤/游标 → room page | PostgreSQL/缓存；无写入 | 键集分页；高容量依赖索引 |
| GET `/internal/monitoring/rooms/{roomId}/runtime` | path → runtime/404 | Redis runtime；无写入 | 可能陈旧或过期 |
| GET `/internal/monitoring/rooms/{roomId}/events` | limit → events | Redis，不足时回源 PostgreSQL并回填 | 读取会写热缓存；历史权威在 PostgreSQL |
| GET `/internal/monitoring/players/{playerId}/room-history` | 游标 → history page | PostgreSQL | 敏感调查数据由 Admin 再做工单审计 |
| GET `/internal/monitoring/players/{playerId}/connection-history` | 游标 → history page | PostgreSQL | 同上 |
| GET `/internal/monitoring/player-presence` | playerIds → presence map | Redis + Lobby store | 实时近似值，不可作为账号权威在线状态 |

## 3. Allocator API

`/internal` 由 `AllocatorServiceAuthenticationMiddleware` 保护：管理终止只接受 Bearer(manage)，实例 GET 可接受 Bearer(service) 或 Bearer(monitor)，其他内部接口接受 Bearer(service)。

| 方法与路径 | 请求 → 响应 | 调用方 | 数据写入 | 幂等策略 | 当前风险 |
|---|---|---|---|---|---|
| GET `/health/live` | 无 → live DTO | 探针 | 无 | 只读 | 仅进程存活 |
| GET `/health/ready` | 无 → backend/state/port/outbox 状态 | 探针 | 可能触发状态检查 | 只读语义 | 本地目录可写不等于 DS 构建可运行 |
| GET `/openapi/v1.yaml` | 无 → YAML | 契约工具 | 无 | 只读 | 需实现漂移检查 |
| POST `/internal/allocations` | `AllocationRequest` → `AllocationResponse`，202 | Lobby | JSON 状态、端口租约、进程或 Agones GameServer | `X-Request-Id` 作为分配请求键 | 网络不确定结果不得无条件重试，否则可能重复实例 |
| GET `/internal/instances` | 无 → instance list | Lobby/Admin | 无 | 只读 | 返回运行拓扑，必须仅内网 |
| GET `/internal/instances/{id}` | path → instance/404 | Lobby/Admin | 无 | 只读 | 同上 |
| POST `/internal/instances/{id}/register` | `ConfirmRegistrationRequest` → registration ack | Lobby 代理 DS 注册 | 实例 Registered、heartbeat credential | registration credential 单次使用 | Lobby/Allocator 两段绑定存在部分成功窗口 |
| POST `/internal/instances/{id}/heartbeat` | `InstanceHeartbeatRequest` → 204 | Lobby 代理 DS 心跳 | 实例最后心跳/状态/遥测 | heartbeat credential + instance id | 频率高；持久 checkpoint 与内存状态有时间差 |
| POST `/internal/instances/{id}/drain` | 无 → instance snapshot | Lobby | Draining/Stopped、释放端口/删除 Agones GS | 已 Draining 可恢复重试 | 调用取消后仍需保证进程终止 |
| POST `/internal/admin/instances/{id}/terminate` | `AdminTerminateInstanceRequest` → result | Admin | 实例终止和审计状态 | `Idempotency-Key` + expected state | 高风险；依赖 Admin 审批和状态快照 |

## 4. PlayerData API

| 方法与路径 | 请求 → 响应 | 认证/调用方 | 数据写入 | 幂等策略 | 当前风险 |
|---|---|---|---|---|---|
| GET `/health/live` | 无 → live | 探针 | 无 | 只读 | 仅进程存活 |
| GET `/health/ready` | 无 → store 状态 | 探针 | 无 | 只读 | 外部投影目标不在 readiness |
| POST `/internal/sources/reward-claims` | `RewardClaimRequest` → record result | Bearer(source)；奖励来源 | grant、balance、transaction/evidence、projection outbox | `Idempotency-Key == eventId`，事务 + 唯一约束 | 资产 POST 不可透明重试，调用方必须保留 eventId |
| POST `/internal/sources/payment-orders` | `RecordEvidenceRequest` → evidence result | Bearer(source) | evidence + projection outbox | key == eventId；来源唯一约束 | 只记录证据，不处理支付结算 |
| POST `/internal/sources/reports` | 同上 | Bearer(source) | evidence + outbox | 同上 | 来源凭据权限范围较宽 |
| POST `/internal/sources/replays` | 同上 | Bearer(source) | replay 元数据 + outbox | 同上 | 不存放实际对象内容 |
| POST `/internal/admin/wallet-operations` | `AdminWalletOperationRequest` → operation result | Bearer(admin command)；Admin | balance、wallet transaction、reward 状态 | command UUID UNIQUE + 行版本/事务 | 仅补偿/撤销奖励；必须保持双人审批 |
| GET `/internal/monitoring/health` | 无 → health | Bearer(monitor)；Admin | 无 | 只读 | 与公开 readiness 分离正确 |
| GET `/internal/monitoring/players/{playerId}/balances` | path → balances | Bearer(monitor)；Admin | 无 | 只读 | 财务数据需 Admin RBAC/ABAC |
| POST `/internal/chat/messages/authorize` | `AuthorizeChatMessageRequest` → policy | Bearer(chat gateway)；聊天网关 | 无 | 无透明重试 | Auth 不可用时 fail closed |

## 5. Admin API

### 5.1 认证模型

- `/admin/v1`：企业 OIDC/JWT 或非生产本地专用 token，经 `AdminAuthenticationMiddleware` 映射 RBAC；高风险资源再由 ABAC 检查区域、班次、案件和 break-glass。
- `/internal/projections`：独立 evidence ingestion token。
- `/internal/topology/registrations`：独立 topology registration token。
- `/health/*`：无认证。

### 5.2 监控与查询

| 方法与路径 | 请求 → 响应 | 主要角色/调用方 | 数据写入 | 幂等与风险 |
|---|---|---|---|---|
| GET `/health/live` | 无 → live | 探针 | 无 | 只读 |
| GET `/health/ready` | 无 → stores 状态 | 探针 | 无 | 检查 Admin 自有存储，不证明全部聚合源可用 |
| POST `/internal/topology/registrations` | `MonitoringSourceRegistration` → lease | Lobby/Allocator | 内存 topology registry | source/generation/lease 覆盖；重启丢失 |
| GET `/admin/v1/me` | 无 → operator/roles/capabilities | 已认证管理员 | 无 | 不返回原 token |
| GET `/admin/v1/overview` | 无 → overview | RoomViewer | 无 | 按来源独立降级；可能带 stale 元数据 |
| GET `/admin/v1/source-health` | 无 → reliability metadata | 房间或玩家监控角色 | 无 | 只读 |
| GET `/admin/v1/topology` | region → leases | RoomViewer + region ABAC | 无 | 内存租约，非持久资产清单 |
| GET `/admin/v1/events` | SSE → Admin realtime envelopes | 已认证管理员 | 无 | 有界 backlog；窗口外要求 resync |
| GET `/admin/v1/rooms` | 过滤/游标 → room page | RoomViewer + region ABAC | 无 | 聚合源故障可返回降级/陈旧 |
| GET `/admin/v1/rooms/{roomId}` | path → room detail | RoomViewer + region ABAC | 无 | 高风险操作前要求新鲜快照 |
| GET `/admin/v1/instances` | 过滤/游标 → instance page | RoomViewer + region ABAC | 无 | 多 Allocator 聚合 |
| GET `/admin/v1/players` | 搜索/游标 → player page | PlayerViewer | 无 | Auth/Lobby/PlayerData 分面独立降级 |
| GET `/admin/v1/players/{playerId}` | player/ticket → detail | PlayerViewer + ABAC | `audit_ledger` 读取审计 | 敏感详情要求工单/审计条件 |
| GET `/admin/v1/players/{playerId}/room-history` | ticket/游标 → page | 授权调查角色 | 读取审计 | 每页读取独立审计 |
| GET `/admin/v1/players/{playerId}/connection-history` | ticket/游标 → page | 授权调查角色 | 读取审计 | 同上 |

### 5.3 管理、审计和调查

| 方法与路径 | 请求 → 响应 | 数据写入 | 幂等/审批 | 当前风险 |
|---|---|---|---|---|
| GET `/admin/v1/action-requests` | limit → actions | 无 | RBAC 过滤 | 只读 |
| POST `/admin/v1/action-requests` | `CreateAdminActionRequest` → action，202 | action request | Idempotency-Key；创建人与审批人后续必须不同 | 只创建，不立即执行 |
| POST `/admin/v1/action-requests/{id}/confirm` | `ConfirmAdminActionRequest` → action | confirmation/version | action 状态机 | 必须二次确认 |
| POST `/admin/v1/action-requests/{id}/approvals` | `ApproveAdminActionRequest` → action | approval、command outbox、audit | 独立审批人 + 版本 + 唯一约束 | 下游部分成功由 Outbox 状态处理 |
| GET `/admin/v1/player-asset-operations` | limit → operations | 无 | 角色限制 | 财务敏感 |
| GET `/admin/v1/audit` | limit → audit records | 无 | 哈希链只读 | 本地库可变性由归档/锚定补强 |
| GET `/admin/v1/command-outbox` | limit → outbox | 无 | 管理角色 | 暴露内部失败原因，必须受限 |
| GET `/admin/v1/cases` | limit → cases | 无 | 按案件类型/角色过滤 | 只读 |
| GET `/admin/v1/cases/{caseId}/evidence-package` | case → package | audit ledger | 案件 + ABAC + 读取审计 | 多源任一不可用时不能伪造完整包 |
| POST `/admin/v1/cases/{caseId}/close` | `CloseAdminCaseRequest` → closed case | case、audit | 版本/不可逆关闭、证据 hash | 错误关闭需新案件纠正，不能直接改历史 |
| GET `/admin/v1/rooms/{roomId}/log-exports/{caseId}` | case → 导出 | audit | 案件授权 | 中央日志不可用时 fail closed |
| GET `/admin/v1/rooms/{roomId}/replays?caseId=` | query → replay list | audit | 案件授权 | 仅元数据/受控访问 |

### 5.4 玩家证据、聊天、回放与 GM

| 方法与路径 | 请求 → 响应 | 数据写入 | 幂等/授权 | 当前风险 |
|---|---|---|---|---|
| POST `/internal/projections/player-evidence` | `IngestPlayerEvidenceRequest` → result | `player_evidence` | key == eventId；来源唯一 | 投影不是源业务权威 |
| POST `/internal/projections/player-chat-access-grants` | `IngestPlayerChatAccessGrantRequest` → result | chat grant | key == grantId | grant 必须短期、按 scope |
| GET `/admin/v1/players/{playerId}/reports` | ticket/limit → records | audit | Risk/Sanction/Approver/Audit 角色 | 敏感读取 |
| GET `/admin/v1/players/{playerId}/asset-changes` | 同上 | audit | Compensation/Approver/Audit | 财务敏感 |
| GET `/admin/v1/players/{playerId}/reward-claims` | 同上 | audit | 同上 | 财务敏感 |
| GET `/admin/v1/players/{playerId}/payment-orders` | 同上 | audit | 同上 | 仅证据投影 |
| GET `/admin/v1/players/{playerId}/chat-permission` | ticket → permission | audit | ChatCompliance/Audit | 只返回权限判断 |
| GET `/admin/v1/players/{playerId}/chat-records` | ticket/time/scope → records | audit | 有效独立 grant + 角色 | 归档故障 fail closed |
| GET `/admin/v1/players/{playerId}/gm-operations` | ticket/limit → records | audit | 管理/制裁/审计角色 | 防止普通运营越权 |
| GET `/admin/v1/players/{playerId}/replays` | case/limit → records | audit | 案件 + Replay 角色 | 元数据读取 |
| GET `/admin/v1/players/{playerId}/replays/{eventId}/access` | case → signed access | 访问审计 | 短期签名、绑定玩家/操作者/过期时间 | 签名密钥必须独立 |
| GET `/admin/v1/players/{playerId}/replay-content/{eventId}` | case/expires/signature → stream | 访问审计 | 验签 + 案件 + 身份 | 大对象限额与流式超时 |

Admin 支持的受控命令包括房间标异常、禁止加入、维护、强制解散，实例终止，玩家下线/冻结/封禁/解禁/禁言/解禁言/会话重置/风险标记，以及补偿/错误奖励撤销、创建调查/客服案件。动作类型白名单不包含“修改对局结果”。

## 6. Join Ticket 校验与 UE 网络入口

1. Lobby 在房间已有 route 且玩家是成员时，用 HMAC 签发短期 Join Ticket。
2. UE Client 将 `PlayerId` 与 URL 编码后的 `JoinTicket` 放入 Unreal travel URL。
3. `AGuiyangMahjongGameMode::PreLogin` 在接受网络连接前调用 `FGuiyangJoinTicketValidator::ValidateAndConsume`。
4. 校验绑定 player、room、match、server instance、过期时间和签名，并在单进程内消费票据防重放。
5. `PostLogin` 使用 PreLogin 暂存的可信 player 绑定 Controller；客户端后续不能通过 RPC 改写身份。

风险：票据单次消费集合是 DS 进程内状态，不跨进程；其安全边界依赖票据同时绑定唯一 server instance。服务器重启后旧票是否仍在有效期内取决于短 TTL 和实例绑定。

## 7. 客户端重连

- HTTP 控制面入口：POST `/v1/reconnect/route`。
- 游戏网络入口：重新 travel 到 DS，携带新 Join Ticket。
- DS 入口：PreLogin 验票，RoomManager 按可信 PlayerId 恢复保留座位和私有状态。
- 当前没有单独的 HTTP “恢复牌局状态”接口，恢复通过 Unreal RPC 快照完成。

## 8. 跨服务调用与重试原则

| 调用 | 当前超时/重试语义 |
|---|---|
| Lobby → Allocator | 有 HTTP 超时；分配不做无条件透明重试 |
| Allocator → Lobby failure | 持久状态并后台重试 |
| Allocator → Lobby settlement recovery | 本地文件 Outbox，Lobby 确认后删除 |
| DS → Lobby register/heartbeat/result | DS 生命周期内重试；结算用固定 sequence/credential |
| Admin → 业务服务命令 | Admin command Outbox；仅可重试被分类为 retryable 的命令 |
| PlayerData → Admin projection | PostgreSQL Outbox + 租约/重试 |
| Admin → 监控源 | 超时、熔断、最后成功快照、按来源降级 |

全局缺口是 `request_id`、`correlation_id`、`trace_id` 尚未在所有服务和 UE HTTP 客户端形成统一三元契约；当前以 W3C Trace、`X-Trace-Id` 和局部 `X-Request-Id` 为主。

## 9. 本阶段变化

阶段 0 未修改任何 API 路径、方法、请求/响应结构、认证、数据库写入或幂等行为。

