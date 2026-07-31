# 阶段 6：Dedicated Server 生命周期、连接认证与恢复

## 1. 阶段范围与仓库基线

本阶段在现有 `GuiyangMahjongServer`、Lobby Join Ticket 签发链和 Allocation Provider 上增量实施，未拆分新服务、未修改麻将规则、未让 Dedicated Server 写玩家资产/战绩，也未实现 GameData 最终结算。

实施前实际发现：

- Server Target 已隔离 ClientOnly 与 Editor 模块；`GuiyangMahjongServer.Build.cs` 不依赖 UMG、Slate 或渲染表现模块。
- 原 Join Ticket 已具备 HMAC-SHA256、实例/房间/Epoch 作用域和进程内一次性 nonce，但缺少 TicketId、座位、Session、Build、协议、规则和签发时间。
- 牌桌引擎已经由 DS 权威校验回合、候选动作、牌所有权和客户端单调序号，但客户端动作缺少 UUID、期望状态版本和 RoomEpoch。
- 已存在 CSPRNG 洗牌种子、开局承诺、牌序摘要和结算后披露；原实现没有把未披露随机状态纳入崩溃恢复。
- 已存在断线宽限、自动托管、重连组合快照和 Agones Ready/Health/Shutdown 适配；原实现没有跨进程权威快照和确定性动作重放。
- 本机 `D:\Program Files\Epic Games\UE_5.8` 是不完整安装，缺少 `Build.bat`、UnrealBuildTool 和 `UnrealEditor-Cmd.exe`，因此不能声称本阶段 UE 编译或 Automation Test 已通过。

## 2. 运行时模块边界

本阶段不为目录美观搬迁旧文件，而是增加实际职责文件：

| 边界 | 实现 | 职责 |
|---|---|---|
| ConnectionAuth | `Server/GuiyangServerTicketVerifier.*` | 强票据字段、HMAC、时间、实例、Epoch、版本、一次性消费 |
| RoomRuntime | `Game/GuiyangMahjongGameMode.*`、`Table/MahjongTableEngine.*` | 动作信封、频率、状态版本、牌/规则合法性、控制权确认 |
| Evidence | `Evidence/GuiyangActionEvidence.*` | 规范动作、previous_hash/action_hash、完整状态哈希引用 |
| Snapshot | `Snapshot/GuiyangRuntimeRecoveryStore.*` | 原子快照、追加动作 JSONL、跨 Epoch 读取和完整性校验 |
| Lifecycle | 现有 GameMode、GameServerBridge、Agones Subsystem | Bootstrap、注册、心跳、恢复后 Ready、Shutdown |
| SettlementClient | 现有 GameServerBridge | 保留结果 Outbox；新增结算前证据强屏障 |
| Telemetry | 现有心跳 | 上报恢复权威、证据就绪和权威状态哈希 |
| Agones | 现有 Agones Subsystem + Fleet | Health/Ready/Allocated/Shutdown 与 RWX 恢复卷 |

`GuiyangMahjongServer` 仍是唯一 ServerOnly 运行模块；共享 `GuiyangMahjongCore` 只增加不含 HTTP、Agones、UI 的牌桌恢复值对象和导入/导出方法。

## 3. Join Ticket 与会话安全

新版 HMAC 载荷包含：

```text
ticketId, playerId, displayName, roomId, matchId, seatId,
sessionId, sessionEpoch, securityEpoch,
serverInstanceId, roomEpoch, clientBuild, protocolVersion,
ruleSetVersion, issuedAtUnixSeconds, expiresAtUnixSeconds, nonce
```

验证顺序为：长度限制 → HMAC 固定时间比较 → JSON 类型 → 玩家/房间/比赛/实例 → RoomEpoch → 座位范围 → Session 格式和 Epoch → 客户端 Build 白名单 → 协议/规则版本 → 签发/过期窗口 → nonce 一次性消费。

Auth 原有访问令牌中的 `Sid/SessionEpoch/SecurityEpoch` 现在会在 Lobby 验签后进入 `PlayerIdentity`，再进入票据。Lobby 的管理员撤销水位仍在签发前执行；DS 不接收或记录 Access Token。`MAHJONG_ALLOW_LEGACY_JOIN_TICKETS=false` 是生产默认值，旧票据兼容只能在受控滚动回滚窗口显式开启。

Lobby 生产默认 `AllowLegacyClientVersionContext=false`。没有 EdgeGateway 清洗后的版本头时，Lobby 返回 426，不签发可进入 DS 的票据。下游 DS 仍独立复核票据版本，不仅信任网关头。

Dedicated Server 的工作负载身份沿用并强化现有每实例注册凭证：注册凭证只使用一次，成功后从内存清除；Lobby 再分别下发心跳和结算短期凭证。所有注册、心跳和 Ready 回调继续携带 `server_instance_id + room_epoch + fencing_token`，旧实例不能覆盖新实例。

## 4. 权威动作与证据链

客户端动作新增：

```text
client_action_id
client_sequence
expected_state_version
room_epoch
client_sent_at_unix_milliseconds
action_type / target / consumed_tile_ids
```

DS 入口依次校验 Controller 绑定身份、座位、恢复确认状态、UUID、RoomEpoch、状态版本、两分钟时间窗、已接受动作去重和每玩家每秒十个意图的入口限流。随后牌桌引擎继续校验当前回合、候选动作、牌所有权、规则合法性和客户端单调序号。

每个已接受玩家动作写入：

```text
match_id, room_id, room_epoch, action_sequence,
state_version_before, state_version_after, state_hash_after,
player_id, seat_id, action_type, normalized_payload,
occurred_at, previous_hash, action_hash
```

超时托管动作也写入证据链。由于一次响应超时可能原子推进多个 Pass，它被标记为不可单条重放，并立即要求完整快照；若该快照失败，新实例会拒绝越过这个恢复缺口，不会伪造确定性结果。

动作证据不包含 Access Token、Refresh Token、Join Ticket 原文、密钥、数据库口令或私有手牌。完整手牌只存在受限快照文件。

## 5. 完整快照、随机状态与恢复

快照包含 Match/Room/Epoch、快照和动作序号、状态版本、规则版本、完整牌桌状态、完整牌墙及游标、四家手牌、候选动作、响应窗口、累计分、托管座位、洗牌种子/nonce/承诺、已披露公平性证明链、完整状态哈希和创建时间。

策略默认：

- 每 3 个合法动作一次，可通过 `MAHJONG_SNAPSHOT_EVERY_ACTIONS=1..5` 覆盖；
- 最长 10 秒，可通过 `MAHJONG_SNAPSHOT_MAX_INTERVAL_SECONDS=1..60` 覆盖；
- 发牌后立即快照；
- 杠、胡、进入 Settlement、自动托管后立即快照；
- 普通快照失败记录错误但不回滚已接受动作；
- 结算前快照失败会把 `settlementEvidenceReady` 置为 false，并阻止正常结算上报。

恢复文件按 `RecoveryDirectory/<match_id>/snapshot-<epoch>.json` 与 `actions-<epoch>.jsonl` 隔离。新实例只读取小于当前 Epoch 的最新快照，校验完整状态哈希和后续动作哈希链，然后逐条调用同一个 `UMahjongTableEngine` 动作接口重放，并逐条比较 `state_version_after + state_hash_after`。成功后：

1. 继承动作序号和 previous hash；
2. 把旧连接统一标为 `GameServerRecovery` 断线，不施加玩家处罚；
3. 恢复回合、牌墙游标、响应窗口、公平性材料和托管座位；
4. 以新 Epoch 立即写快照；
5. 重建权威计时器；
6. 若崩溃点在 Settlement，则继续证据屏障和幂等结算上报；
7. 玩家使用新 Epoch Ticket 重连；
8. 客户端应用公共/私有状态后回传一次控制令牌、状态版本和哈希；确认前恢复实例拒绝该连接的新动作。

旧 DS 即使重新运行，也只能写自己的旧 Epoch 文件；其 Ticket、动作、心跳和 Ready 都因 RoomEpoch/Fencing 过期失去权威资格。

## 6. 部署与配置

### LocalProcess

Allocation Service 新增 `Allocator:RecoveryDirectory`，启动 DS 时转换为绝对目录并注入：

```text
MAHJONG_RECOVERY_DIRECTORY
MAHJONG_SNAPSHOT_EVERY_ACTIONS=3
MAHJONG_SNAPSHOT_MAX_INTERVAL_SECONDS=10
MAHJONG_COMPATIBLE_CLIENT_BUILDS=1.0.0
```

Compose 使用 `/var/lib/guiyang-mahjong/recovery`，位于现有 Allocator 持久卷。

### Agones

`Deploy/Agones/game-recovery-pvc.yaml` 声明 `ReadWriteMany` PVC，Fleet 挂载到 `/var/lib/guiyang-mahjong/recovery`。生产集群必须选择真正支持 RWX、静态加密、访问审计和快照备份的 StorageClass；不满足这些条件时不得宣称具备跨节点恢复能力。

完整手牌快照的 Kubernetes/RWX 卷读取权限只授予 GameServer 工作负载身份和受控调查/恢复任务，普通运营、Admin 列表接口和日志采集器均不得挂载。

## 7. API、数据库、Redis 与事件变化

- 外部 HTTP 路径和正常响应结构未删除、未重命名。
- Join Ticket 是短期内部契约的向后兼容字段扩展；严格 DS 默认拒绝旧载荷，回滚开关见下节。
- UE RPC `Server_RequestAction` 的结构增加字段；旧 `Server_RequestPlayTile` 只保留本地非托管兼容，托管 DS 显式拒绝。
- 新增内部 UE RPC `Server_ConfirmReconnectState`，它只确认客户端已应用状态，不提交权威结果。
- 本阶段无 PostgreSQL Schema/迁移、无业务表所有权变化、无 Redis 键变化、无正式消息总线事件变化。
- 心跳 JSON 兼容增加 `recoveredAuthority`、`settlementEvidenceReady` 和 `authoritativeStateHash`；旧 Lobby JSON 反序列化会忽略未知字段。

## 8. 回滚

1. 客户端和 Lobby 先回滚到旧 Ticket/动作契约；
2. 短期设置 `MAHJONG_ALLOW_LEGACY_JOIN_TICKETS=true`，仅覆盖一个 Ticket 最长生命周期；
3. 必要时设置 `Lobby__AllowLegacyClientVersionContext=true` 恢复旧直连，但不得长期保留；
4. 回滚 Fleet 时保留 `mahjong-game-recovery` PVC，不删除证据；
5. 回滚 DS 二进制后，旧版本会忽略新 Epoch 文件，不会修改 PostgreSQL 或玩家资产；
6. 完成回滚后恢复强校验开关，并记录变更、审批和证据保留决定。

## 9. 验证与已知限制

已增加 UE Automation Test 源码入口：

- `GuiyangMahjong.GameServer.JoinTicketValidation`
- `GuiyangMahjong.GameServer.Snapshot.RoundTripAndHash`
- `GuiyangMahjong.GameServer.Evidence.HashChain`
- `GuiyangMahjong.GameServer.Snapshot.CrossEpochLoad`

测试覆盖强 Ticket、重放、错误实例、过期、状态导出/导入、哈希一致和跨 Epoch 读取。原牌桌测试继续覆盖非法动作、重复序号、规则合法性、断线/重连和私有手牌定向 RPC。

当前限制：

- 因 UE 5.8 工具链缺失，新增 UE 测试尚未实际编译执行；这是进入生产前的硬阻塞项。
- RWX PVC 是恢复热存储，不是长期 ReplayEvidence 归档；后续阶段应迁移到受控对象存储并建立保留/删除策略。
- Session 撤销在 Ticket 签发前由 Lobby 校验，Ticket 30 秒生存期内的即时撤销依赖控制面断连事件；后续应把版本化 `SessionRevoked` 可靠推送到活跃 DS，而不是依赖普通日志或轮询。
- 当前不实现 DS 双活；任何恢复都先由 RoomControl 增加 Epoch，再启动单一新权威实例。

## 10. 2026-07-31 验证记录与阶段判定

| 验证项 | 结果 | 证据/限制 |
|---|---|---|
| `dotnet restore --locked-mode` | 通过 | 所有解决方案项目依赖均为最新，无锁文件漂移。 |
| `dotnet build --no-restore` | 通过 | 0 警告、0 错误。 |
| `dotnet test --no-build --no-restore` | 通过 | 256 项：233 通过、23 项按环境条件跳过、0 失败；跳过项依赖外部 PostgreSQL/Redis。 |
| Lobby/Allocator JSON | 通过 | 使用 UTF-8 JSON 解析；PowerShell 5.1 默认代码页不适合校验无 BOM 中文文件。 |
| Docker Compose | 通过 | `docker compose ... config --quiet` 返回成功。 |
| Kubernetes/Agones YAML | 通过（语法） | 三份本阶段相关清单均可完整反序列化。 |
| Kubernetes OpenAPI/CRD | 未完成 | 本机没有可访问集群；`kubectl` 无法取得 Agones CRD OpenAPI。 |
| Helm | 不适用 | 仓库没有本阶段 Helm Chart，且本机未安装 Helm。 |
| Target/Build.cs 静态边界 | 通过 | Client 未引用 Server/Agones；Server 未引用 Client/EditorTools/UMG/Slate；Core 未引用 UI、HTTP、Agones 或编辑器模块。 |
| UE Client/Server 编译 | 阻断 | `D:\Program Files\Epic Games\UE_5.8` 缺少 Build.bat、UnrealBuildTool。 |
| UE Automation Tests | 阻断 | 同一安装缺少 RunUAT.bat、UnrealEditor-Cmd.exe，新增测试尚未真实运行。 |
| `git diff --check` | 通过 | 当前未提交差异无空白错误。 |

结论：源代码、配置和非 UE 自动化基线已经完成；由于 UE 5.8 工具链不完整，阶段 6 暂不能判定为最终验收通过。补齐引擎后必须先构建 `GuiyangMahjongClient` 与 `GuiyangMahjongServer`，再执行上述四个 GameServer 自动化测试和既有牌桌/网络测试；全部通过后方可进入生产部署验收。
