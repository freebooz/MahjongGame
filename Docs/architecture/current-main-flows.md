# 当前核心业务流程

> 盘点日期：2026-07-31。流程依据实际 API、服务、存储和 UE C++ 调用关系整理。  
> 表中“临时状态”指 Redis、进程内状态、文件 Outbox 或短期凭据；“审计”只描述当前已有机制。

## 1. 身份与会话

### 1.1 游客登录

1. UE `GuiyangLoginSubsystem` 调用 Auth `POST /v1/auth/guest`。
2. Auth 校验 installation/device/display name，使用服务器 pepper 计算 installation hash 和稳定 player id。
3. `GetOrCreateGuestAsync` 写/读 `auth_identities`；创建 refresh session，记录脱敏 login event。
4. Auth 签发短期 access token 和一次 refresh token，UE 仅持久化允许的登录状态。

- 数据写入：identity、refresh session、login event。
- 临时状态：access/refresh token；Auth 不存 refresh 明文，只存 hash。
- 失败/重试：限流、格式错误或存储失败明确返回；网络不确定时重试可能产生额外 refresh session。
- 幂等：installation hash 主键保证身份稳定，但“创建会话”不是请求级幂等。
- 审计：脱敏 IP、device id、client summary、outcome。
- 风险：缺少登录请求 Idempotency-Key；设备身份完全依赖 pepper 保密。

### 1.2 Token 刷新

1. UE 调 `POST /v1/auth/refresh`。
2. Auth 解析 session id 和 token secret，计算 hash。
3. PostgreSQL 事务锁定旧 session，校验未撤销、未过期、账号未冻结/封禁。
4. 原子撤销旧 session 并插入新 session，签发新 access/refresh。

- 数据写入：旧 session `revoked_at`、新 session。
- 临时状态：响应中的新 token。
- 失败/重试：旧 token 只能使用一次；响应丢失后同 token 重试失败。
- 幂等：单次轮换而非“同响应重放”。
- 审计：会话可由 Admin 查询；没有独立 refresh event 表。
- 风险：客户端必须把网络不确定刷新视为重新登录，不得透明重试。

### 1.3 登出和会话撤销

用户登出：

1. UE 调 `POST /v1/auth/logout`。
2. Auth 验证 refresh token hash，标记 session 撤销；无效/已撤销请求安全完成。

管理撤销：

1. Admin 创建 action → 二次确认 → 独立审批。
2. Admin command Outbox 调 Auth sessions/revoke；需要时再调 Lobby disconnect。
3. Auth 以 command id 幂等撤销该玩家 sessions。
4. Lobby 写 Redis `access-revoked-before`、移除 Presence 并断开 WebSocket。

- 数据写入：Auth session、admin command/control event、Admin action/approval/outbox/audit。
- 临时状态：Lobby Redis 撤销水位和连接。
- 失败/重试：Admin Outbox 记录分步结果并只重试可重试错误。
- 幂等：command id、Idempotency-Key。
- 审计：操作人、审批人、理由、前后状态、TraceId、工单。
- 风险：Auth 成功而 Lobby 失败时会短暂部分完成；Redis 丢失可能削弱旧 access token 撤销。

## 2. 房间控制面

### 2.1 创建好友房

1. UE 用玩家 Bearer、UUID RequestId 和 Idempotency-Key 调 Lobby `POST /v1/rooms`。
2. Lobby 验证规则/密码，检查玩家活动房间。
3. 生成随机六位 room code、RoomId、MatchId 和不可变规则快照。
4. PostgreSQL 事务插入 `lobby_rooms` 与 `active_player_rooms`。
5. 发布房间事件；若启用 Allocator，调用分配并把实例 ID 写入 pending binding。

- 数据写入：房间快照、活动租约；后续分配绑定。
- 临时状态：Redis 幂等锁/响应和房间缓存。
- 失败/重试：房间号冲突有限重试；Allocator 失败将房间置 Failed。
- 幂等：Redis 结果 + room/player 唯一约束。
- 审计：结构化日志、房间事件；普通创建不进入 Admin 审计账本。
- 风险：数据库提交与事件/Allocator 不在同一 Outbox 事务。

### 2.2 加入房间

1. UE 调 `POST /v1/rooms/{roomCode}/join`。
2. Lobby 校验协议、房间状态、密码、防暴力限制和玩家活动租约。
3. PostgreSQL 原子添加成员并占用 `active_player_rooms`。
4. 若 DS 已注册，签发 route/Join Ticket；否则返回 Allocating/Waiting 操作结果。

- 数据写入：房间 JSONB 成员、活动租约；触发器投影 room history。
- 临时状态：密码失败窗口、Redis 幂等结果/缓存。
- 失败/重试：满员、禁加入、密码错误、其他活动房间明确失败。
- 幂等：玩家 + 房间 + key；成员集合和主键兜底。
- 审计：房间更新事件/日志。
- 风险：密码限流是进程内实现时不能跨实例统一；Redis 模式幂等锁无 fencing。

### 2.3 关闭房间

1. 房主调 `POST /v1/rooms/current/close`。
2. Lobby 读取活动房间并校验 owner。
3. Waiting/Allocating 转 Closed，Playing 转 Failed；清 route/pending，保留 last instance。
4. CAS 更新房间，释放活动成员租约并发布关闭事件。
5. HTTP 完成后调用 Allocator drain；失败只记录待回收风险。

- 数据写入：房间终态、历史投影。
- 临时状态：Redis 幂等结果、缓存、DS 实例。
- 失败/重试：状态冲突最多 3 次；drain 可由实例监控继续回收。
- 幂等：玩家 + key；终态检查。
- 审计：房间事件和结构化日志；管理强解散另有 Admin 审计。
- 风险：回收在业务提交之后，可能出现终态房间仍占 DS。

### 2.4 Dedicated Server 分配

1. Lobby 房间创建成功后调用 Allocator `POST /internal/allocations`，传播 RequestId/TraceId。
2. Allocator 根据 LocalProcess 或 Agones 后端创建实例。
3. LocalProcess：保留端口、生成一次性 registration credential/heartbeat secret、持久化 JSON 状态并启动进程。
4. Agones：创建 Allocation 请求，取得 GameServer 地址/端口并记录实例。
5. Lobby 保存 `PendingServerInstanceId`，等待 DS 注册。

- 数据写入：Allocator 状态文件/内存；Lobby pending binding。
- 临时状态：端口租约、进程、Agones GameServer、实例凭据。
- 失败/重试：RequestId 稳定；超时不能盲目创建第二实例。
- 幂等：Allocator request id/状态记录。
- 审计：结构化日志、实例监控。
- 风险：两个服务没有共享事务；分配成功、Lobby 写入失败可能产生孤儿实例。

### 2.5 DS 子进程启动

1. Allocator 组装 Server executable、地图、监听端口、room/match/instance、控制面 URL、一次性凭据、Join Ticket 密钥/Agones 模式参数。
2. `GameServerProcessLauncher` 创建子进程；Linux 使用进程组和 SIGTERM 优雅终止。
3. Allocator 记录 PID、端口、启动时间、注册截止时间。
4. Instance monitor 检测退出、注册超时和心跳超时。

- 数据写入：Allocator JSON checkpoint、结算恢复目录预留。
- 临时状态：OS 进程、端口、命令行/环境注入的安全配置。
- 失败/重试：启动失败回收端口；异常退出持久化失败通知并重试回调。
- 幂等：同实例状态机防重复启动。
- 审计：实例结构化日志。
- 风险：敏感启动参数必须避免出现在进程列表；Windows SYSTEM/hostPath 部署权限过大。

### 2.6 DS 注册

1. DS 地图监听就绪后，`GuiyangGameServerBridge` 调 Lobby register。
2. Lobby 等待 pending room binding，校验 room/match/instance。
3. Lobby 代理调用 Allocator register，Allocator 消费一次性 registration credential，返回 heartbeat credential。
4. Lobby 将房间转 Waiting、写 route、生成并只保存 result credential hash。
5. Lobby 向 DS 返回 heartbeat/result credentials 和权威 `ManagedRoomBootstrap`。
6. DS 只用 Bootstrap 创建唯一托管房间，然后开始心跳。

- 数据写入：Allocator Registered、Lobby route/result hash。
- 临时状态：DS 内存中的 credentials/bootstrap。
- 失败/重试：注册 credential 单次；重复/错误实例被拒。
- 幂等：房间 pending 状态和一次性 credential。
- 审计：ServerAssigned/RoomUpdated 事件、Trace。
- 风险：Allocator 已确认但 Lobby route 写失败是两阶段部分成功窗口。

### 2.7 DS 心跳

1. DS 按返回间隔提交实例、房间、玩家连接、延迟、CPU/内存、网络、Tick/帧、RPC、异常与结算遥测。
2. Lobby 先调用 Allocator heartbeat 验证 heartbeat credential 并续实例状态。
3. Lobby 校验 telemetry schema/version/成员集合，更新 Redis runtime。
4. lifecycle 合法转换时 CAS 更新 PostgreSQL 房间；连接变化写追加事件历史。
5. 空房超过超时后关闭/失败房间并 drain。

- 数据写入：Allocator 状态、Lobby room、`room_event_history`。
- 临时状态：Redis runtime、事件热缓存。
- 失败/重试：下一心跳覆盖 runtime；EventId/状态序列抑制重复。
- 幂等：实例 credential、EventId、CAS。
- 审计：结构化日志、Trace、房间时间线。
- 风险：Lobby→Allocator 同步依赖增加心跳尾延迟；runtime 不持久。

### 2.8 Join Ticket 签发

1. 玩家加入后或主动查询 route/reconnect route。
2. Lobby 以 Auth 身份为准，确认玩家属于房间且 route 可用。
3. HMAC 票据绑定 PlayerId、RoomId、MatchId、ServerInstanceId、显示名和过期时间。
4. 响应只返回给已认证成员。

- 数据写入：无。
- 临时状态：短期 Join Ticket。
- 失败/重试：可重新签票。
- 幂等：非幂等，每次生成新票。
- 审计：请求 Trace/日志，不记录票据。
- 风险：GET route 产生凭据；必须禁止缓存和日志记录响应。

### 2.9 Join Ticket 校验与玩家进入 DS

1. UE travel 到 route 的 IP/UDP 端口，携带 URL 编码 PlayerId/JoinTicket。
2. DS `PreLogin` 在创建 PlayerController 前校验签名、绑定范围、过期和 player，并消费票据。
3. 可信 claims 以 ticket digest 暂存到连接。
4. `PostLogin` 把可信 player 绑定 Controller，托管房间接纳/恢复座位，Agones 记录 player connect。
5. DS 通过 Client RPC 下发公共/私有状态；客户端只提交操作意图。

- 数据写入：无数据库；DS 房间内存。
- 临时状态：已消费 ticket 集合、连接绑定、牌局状态。
- 失败/重试：无效/重放票在 PreLogin 拒绝；客户端回 Lobby 取新 route。
- 幂等：单进程单次消费 + player/seat 绑定。
- 审计：连接事件随心跳投影。
- 风险：已消费集合不持久；依赖短 TTL 和实例绑定阻止跨重启重放。

## 3. 断线、重连与托管

### 3.1 玩家断线

1. UE 网络连接断开触发 GameMode `Logout`。
2. DS 先把 seat 标记 NetworkInterrupted 并保留到规则快照的 reconnect timeout。
3. 移除连接级授权和 Agones player tracking，但不立即删除托管房间座位。
4. 心跳上报连接状态变化。

- 数据写入：后续 Lobby 追加连接事件。
- 临时状态：DS 离线座位、断线时间。
- 失败/重试：DS tick/房间管理器处理超时。
- 幂等：重复断开按当前状态处理。
- 审计：连接时间线。
- 风险：DS 进程崩溃会丢失未持久牌局状态，目前无牌局状态恢复。

### 3.2 玩家重连

1. UE 调 Lobby reconnect route，Lobby 忽略客户端过期 room/match 提示，以活动房间映射为准。
2. Lobby 签发新 Join Ticket。
3. UE 重新连接 DS，PreLogin 验票并绑定同 PlayerId。
4. RoomManager 校验保留座位未超时，标记 Reconnected。
5. DS 只向该 Controller 下发私有手牌、公共状态和可选动作快照。

- 数据写入：连接事件历史由心跳投影。
- 临时状态：新票、DS reconnect snapshot。
- 失败/重试：route/DS 不可用明确失败；可在超时内重试取新票。
- 幂等：同 player 恢复原 seat。
- 审计：连接时间线和 Trace。
- 风险：reconnect route 仅要求 Idempotency-Key 格式，不缓存结果。

### 3.3 托管

1. 权威 TableEngine 检测行动/响应窗口超时。
2. 若规则启用 timeout autoplay，由服务端选择安全动作并推进状态。
3. GameMode 将实际超时且有操作权的 seat 标为 trustee，记录变化时间。
4. 玩家下一次成功提交合法权威动作时解除 trustee；非法请求不解除。
5. 心跳上报 trustee 状态。

- 数据写入：连接/房间事件历史可记录状态变化。
- 临时状态：DS `TrusteeStateByPlayer`。
- 失败/重试：纯服务端确定性推进。
- 幂等：相同 trustee 值不重复刷新变化时间。
- 审计：遥测和时间线。
- 风险：托管决策没有独立长期操作日志，只依赖事件/牌局回放。

## 4. 牌局结束与结算

### 4.1 对局结束

1. TableEngine 进入 Settlement，生成单局结算。
2. GameMode 向各玩家下发结算 RPC。
3. RoomManager `FinishRound` 累加分数、局数，决定 WaitingNextRound 或最终 Settlement。
4. 最后一局构建 `FMahjongFinalSettlementResult`。
5. 公平性模块在发牌前保存 commitment，结算后形成 seed/牌序摘要/事件链证明；证明失败保持 Pending。

- 数据写入：托管 DS 的公平性 Outbox/本地文件（当前工作树已有相关未提交实现）。
- 临时状态：DS 牌桌与房间内存。
- 失败/重试：最终证明未就绪则不提交最终结算。
- 幂等：状态序列、最后已发布/已完成序列。
- 审计：回放、事件链 digest、公平性证明。
- 风险：阶段 0 基线包含未提交公平性改动，尚不是干净发布基线。

### 4.2 结算上报

1. DS 以固定 match/result sequence、实例 result credential 调 Lobby result。
2. Lobby 校验 room/match/instance、credential、lifecycle、完成局数、玩家集合、排名/座位唯一性、公平性证明。
3. PostgreSQL `FinalizeMatchAsync` 在事务中写 match result 并关闭房间。
4. 重复相同 payload 返回 duplicate accepted；相同键不同 payload 返回 conflict。
5. Lobby 更新 runtime 为 Completed，调用 Allocator drain。

- 数据写入：`match_results`、`lobby_rooms`、历史/运行态。
- 临时状态：DS 结算请求、Allocator Outbox recovery file。
- 失败/重试：结果已存而 drain 失败返回 503；DS 用同 sequence 重试，得到 duplicate。
- 幂等：复合主键 + payload 比较 + credential。
- 审计：结果摘要、公平性证明、房间事件和 Trace。
- 风险：Lobby 是结算持久化所有者，尚无独立 Settlement 模块/消费端 Inbox。

### 4.3 战绩保存

服务端权威战绩：

1. Lobby 将完整 `MatchResultReport` JSONB 写 `match_results`。
2. Admin 可通过 Lobby 监控/调查读取摘要与事件。

客户端本地历史：

1. UE Client 收到最终结算 RPC。
2. Client `GuiyangMatchHistorySubsystem` 写本地 SaveGame 供展示。

- 数据权威：Lobby PostgreSQL；客户端 SaveGame 仅展示缓存。
- 失败/重试：服务端按结算幂等；客户端本地写失败不影响权威结果。
- 审计：match/room/result sequence 和事件 digest。
- 风险：当前未见独立公开“玩家战绩查询 API”；客户端跨设备历史能力有限。

## 5. Admin 流程

### 5.1 Admin 查询房间

1. Angular 调 Admin `/admin/v1/rooms` 或 room detail。
2. Admin 验证 OIDC/local principal、RoomViewer RBAC 和 region ABAC。
3. Aggregation 调 Lobby rooms/runtime/events，并聚合 Allocator 实例。
4. 监控可靠性层执行超时、熔断、最后成功快照和来源级降级。
5. Angular 按数据源显示 source、最后成功时间、age、stale threshold。

- 数据写入：通常无；敏感证据读取会写 audit。
- 临时状态：Admin 内存快照、熔断状态、SSE backlog。
- 失败/重试：GET 可由 UI 刷新；单源失败不应整页白屏。
- 幂等：只读。
- 审计：普通列表有限；调查导出/详情有更强读取审计。
- 风险：拓扑和可靠性快照重启丢失；高风险命令前必须拒绝 stale。

### 5.2 Admin 查询玩家

1. Angular 调 Admin players/detail/history/evidence。
2. Admin 验证 PlayerViewer/专项角色、ABAC、ticket/case。
3. 聚合 Auth 身份/会话/控制、Lobby presence/room/connection、PlayerData balances、Admin evidence/cases。
4. 敏感历史和证据读取追加 audit ledger。

- 数据写入：读取审计。
- 临时状态：聚合缓存/来源熔断。
- 失败/重试：分面降级；敏感归档不可用时 fail closed。
- 幂等：读取审计每次独立追加。
- 审计：operator、ticket、TraceId、证据类型和来源引用。
- 风险：数据跨多个时间基准，必须展示新鲜度而非伪装强一致。

### 5.3 Admin 终止房间

1. 操作员读取新鲜 room detail。
2. 创建 `ForceDissolveRoom` action，提供原因和关联工单。
3. 同一操作员二次确认；独立 approver 审批，校验 session fingerprint/状态版本。
4. Admin 事务写 approval、hash-chained audit 和 command outbox。
5. Dispatcher 以专用管理凭据和 Idempotency-Key 调 Lobby room controls。
6. Lobby 比较 expected state sequence，将房间置 Failed/禁新玩家，发布事件并 drain DS。
7. Admin 更新 action/outbox 前后状态。

- 数据写入：Admin action/approval/audit/outbox，Lobby room/history，Allocator 实例。
- 临时状态：dispatcher lease、下游 HTTP。
- 失败/重试：仅 retryable 错误延迟重试；状态冲突失败关闭并要求重新审批。
- 幂等：action id、outbox 唯一、Lobby key、expected sequence。
- 审计：完整保存操作人、时间、原因、前后状态、审批、TraceId、工单。
- 风险：跨三个服务非原子；依赖状态机和调查闭环处理部分成功。

## 6. 当前统一风险

1. 控制面与实时 DS 之间没有牌局状态容灾，DS 进程结束即丢局内内存。
2. `request_id`、`correlation_id`、`trace_id` 尚未全链路统一。
3. Lobby 房间快照、事件发布、Allocator 调用不是统一事务 Outbox。
4. Redis 撤销水位不可恢复，幂等锁无 fencing。
5. 结算仍由 Lobby 持久化，尚未满足最终目标中的独立 Settlement 所有权。
6. 当前公平性实现处于脏工作树，进入下一阶段前必须形成可复现提交并重跑完整验证。

