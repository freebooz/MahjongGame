# 阶段 8：PlayerData 混合职责拆解

## 1. 执行边界

阶段 8 严格拆成 8.1～8.6 六个独立提交和验收门。任一步骤未完成编译、测试、数据核验与回滚验证时，不进入下一步；奖励与资产等高风险写路径不得与其他迁移并行切换。

本文档记录 2026-07-31 对实际仓库的检查结果。阶段 8.1 只冻结已经成立的玩家资料和会话所有权，不修改 API、数据库结构或运行行为。

## 2. 实际实现盘点

### 2.1 项目与依赖

- 生产项目：`Services/GuiyangMahjong.PlayerData/GuiyangMahjong.PlayerData.csproj`。
- 测试项目：`Services/GuiyangMahjong.PlayerData.Tests/GuiyangMahjong.PlayerData.Tests.csproj`。
- 生产 NuGet 依赖为 `Npgsql`；同时引用共享 Observability 项目。
- 未引用 Redis 客户端，也没有 Redis Key、TTL、锁或 Redis 权威状态。
- 持久化模式支持 `InMemory` 和 `Postgres`；生产环境禁止运行时自动执行 DDL。

### 2.2 真实 API

| 方法 | 路径 | 写入/调用 | 幂等与认证 | 目标归属 |
|---|---|---|---|---|
| GET | `/health/live` | 无 | 匿名存活检查 | 短期适配层 |
| GET | `/health/ready` | 数据库探测 | 匿名就绪检查 | 短期适配层 |
| POST | `/internal/sources/reward-claims` | 奖励、余额、证据、Outbox 同事务 | 来源令牌；`Idempotency-Key=eventId` | Economy/Rewards |
| POST | `/internal/sources/payment-orders` | 支付证据、Outbox | 来源令牌；`Idempotency-Key=eventId` | Economy 支付读模型 |
| POST | `/internal/sources/reports` | 举报证据、Outbox | 来源令牌；`Idempotency-Key=eventId` | TrustSafety/Risk |
| POST | `/internal/sources/replays` | 回放证据索引、Outbox | 来源令牌；`Idempotency-Key=eventId` | GameData/ReplayEvidence |
| POST | `/internal/admin/wallet-operations` | 补偿或撤奖、余额、交易、证据、Outbox 同事务 | Admin 命令令牌；`Idempotency-Key=commandId`；双人审批 | Economy/Inventory |
| GET | `/internal/monitoring/health` | 数据库探测 | 只读监控令牌 | Backoffice 依赖健康 |
| GET | `/internal/monitoring/players/{playerId}/balances` | 读取余额 | 只读监控令牌 | Economy/Inventory |
| POST | `/internal/chat/messages/authorize` | 调用 Auth 玩家控制查询 | Chat 专用令牌；失败关闭 | Community/Chat |

EdgeGateway 仍配置 `/api/v1/player-data/**` 到 PlayerData，但当前服务没有对应的公开玩家资料端点。该路由是兼容残留，不能据此认定 PlayerData 拥有玩家资料。

### 2.3 PostgreSQL

唯一 Schema 为 `player_data`：

| 表 | 主键/唯一约束 | 当前写入者 | 当前业务含义 | 目标写入者 |
|---|---|---|---|---|
| `wallet_balances` | `(player_id, asset_code)` | PlayerData | 非负资产余额和版本 | Economy/Inventory |
| `reward_grants` | `reward_grant_id` | PlayerData | 已领取/已撤销奖励 | Economy/Rewards |
| `wallet_transactions` | `transaction_id`；`command_id` 唯一 | PlayerData | 经审批补偿与撤奖流水 | Economy/Inventory |
| `evidence_events` | `event_id`；`(evidence_type, source_reference)` 唯一 | PlayerData | 五类不同所有者的证据混合表 | 按证据类型拆分 |
| `projection_outbox` | `event_id`，外键到证据 | PlayerData | 向 Admin 投影证据 | 各新所有者的 Outbox |

生产运行身份经角色激活后可对整个 `player_data` Schema 执行 DML；迁移身份独立持有 DDL。当前没有多个服务直接写上述表，但单个运行身份权限范围仍会随职责迁移逐步缩减。

### 2.4 后台任务与服务调用

- `PlayerDataStoreInitializer`：初始化持久化；生产环境不得执行迁移。
- `ProjectionDispatcherService`：领取 `projection_outbox` 租约，向 Admin 的 `/internal/projections/player-evidence` 投影。
- PlayerData → Auth：查询 `/internal/monitoring/players/{playerId}`，用于聊天禁言判断。
- PlayerData → Admin：投影举报、资产、奖励、支付和回放证据。
- Admin → PlayerData：执行 `/internal/admin/wallet-operations` 以及余额和健康查询。
- 仓库内未发现 Lobby、Dedicated Server 或 Auth 直接调用 PlayerData 写接口；来源写接口的部署外调用方仍需通过访问日志和调用量指标在切换前确认。

### 2.5 配置

配置包括持久化模式、PostgreSQL 连接、四类入站专用令牌、Auth 查询地址与令牌、Admin 投影地址与令牌，以及 Outbox 轮询/重试参数。迁移期间不得记录任何令牌或数据库口令。

## 3. 风险登记

| 等级 | 风险 | 控制措施 |
|---|---|---|
| 高 | `evidence_events` 混合五类所有者，无法整表一次迁移 | 按 `evidence_type` 分批复制、核对和停写 |
| 高 | 奖励领取同时更新奖励、余额、证据和 Outbox | 奖励与资产在 8.3 同一事务边界内迁移，不做长期双写 |
| 高 | 仓库外来源调用方尚未由代码静态扫描识别 | 切换前统计旧接口调用量、调用方和幂等键 |
| 中 | Admin 直接同步调用 PlayerData 钱包命令 | 8.3 改为 Economy 权威命令，保留有期限适配 |
| 中 | 聊天授权依赖 PlayerData 转发 Auth 控制状态 | 8.4 迁至 Community，并维持失败关闭 |
| 中 | PlayerData 运行角色拥有整个 Schema 的 DML | 每次迁移后撤销已迁表权限并验证生产身份 |
| 中 | EdgeGateway 存在无真实资料 API 的残留路由 | 兼容流量归零后在 8.6 删除或改写 |

## 4. 六个独立验收门

1. **8.1 玩家资料迁移**：确认资料和 Session 已由 Identity 独占；增加只读核验和架构防回流测试。本步骤不产生数据写入。
2. **8.2 战绩与证据迁移**：仅把 `Replay` 类型证据索引迁至 GameData/ReplayEvidence；GameRecords 和 Settlement 已由阶段 7 持有，不触碰举报、资产、奖励和支付证据。
3. **8.3 奖励与资产迁移**：建立 Economy 的 Rewards/Inventory 边界，同一停写窗口迁移钱包、奖励和相关财务证据；Admin 命令切换后关闭旧写入口。
4. **8.4 聊天授权迁移**：建立 Community/Chat 授权边界；旧端点只转发兼容响应并记录废弃调用量。
5. **8.5 管理读模型迁移**：Admin 只消费新所有者的事件建立 Backoffice 读模型；举报证据迁至 TrustSafety/Risk；Admin 不写其他模块业务表。
6. **8.6 下线 PlayerData**：确认旧写流量为零，撤销数据库权限并停部署；若兼容期未结束，仅保留无数据库写权限、带截止日期的适配层。

## 5. 8.1 玩家资料与会话边界冻结

### 5.1 当前实现与数据映射

| 项目 | 源 | 目标 | 处理 |
|---|---|---|---|
| 玩家基础资料 | PlayerData 无源表、无 API | `player.player_profiles` | 已由 Identity/Players 独占，无数据搬迁 |
| 在线 Session | PlayerData 无源表、无 API | `session.auth_refresh_sessions` | 已由 Identity/Sessions 独占，无数据搬迁 |

`player.player_profiles.player_id` 外键关联 `auth.auth_identities.player_id`。资料字段包括显示名、头像、地区、等级、设置、隐私设置和更新时间。PlayerData 的五张表均不包含长期资料或会话凭证。

### 5.2 变更范围

- API：无新增、无删除、无响应变化；EdgeGateway 路由不在本步骤修改。
- 数据库：不新增表、不修改列、不执行 DML；新增只读核验脚本。
- Redis：无变化。
- 消息事件：无变化。
- 配置：无变化。
- 兼容：现有调用路径完全保持。
- 回滚：回退本步骤文档和架构测试即可；核验 SQL 为只读事务并主动回滚。

### 5.3 数据核验与失败处理

执行 `Docs/architecture/sql/stage-8.1-player-profile-validation.sql`。脚本验证：

1. Identity 的资料表和会话表存在；
2. PlayerData Schema 不存在资料或会话表；
3. 每个 `auth.auth_identities` 记录都有对应 `player.player_profiles`；
4. 返回身份数和资料数用于工单留档。

任何一项失败都应停止验收并修复 Identity 数据。禁止通过在 PlayerData 新建资料表来规避失败。

### 5.4 8.1 验收结论

2026-07-31 已完成技术验收：

- `dotnet restore Services/GuiyangMahjong.Services.slnx`：通过；所有项目依赖均为最新。
- `dotnet build Services/GuiyangMahjong.Services.slnx --no-restore`：通过，0 个警告、0 个错误。
- `dotnet test Services/GuiyangMahjong.Services.slnx --no-build --no-restore`：通过 254、跳过 23、失败 0。跳过项均为需要外部 PostgreSQL 或 Redis 地址的既有集成测试。
- 架构测试新增 `PlayerData_DoesNotOwnPlayerProfilesOrSessions`，所在测试程序集共 11 项全部通过。
- 在一次性 PostgreSQL 17 容器中加载 Auth 与 PlayerData 完整 Schema 后执行只读核验：通过；空基线的身份数和资料数均为 0，事务按设计回滚，容器已删除。
- Angular、Docker Compose、Kubernetes、Helm 与 Unreal 文件均未修改，本步骤不重复执行这些无关构建。

技术结论：8.1 达到“资料与会话由 Identity 独占、PlayerData 无旧写入口、无长期双写”的验收条件。真实环境的数据数量核验必须由具备授权的迁移人员运行同一只读脚本并将结果附到工单；本次未读取任何环境密钥或生产凭据。

8.1 未经人工验收前不实施 8.2。

## 6. 8.2 战绩与回放证据迁移

### 6.1 实际边界

GameRecords 与权威 `replay.evidence_manifests` 已由阶段7的 GameData 独占。本步骤只迁移 PlayerData
`evidence_events` 中 `evidence_type='Replay'` 的旧玩家回放索引，不触碰 Report、PaymentOrder、
RewardClaim、AssetChange、钱包、奖励或聊天授权。旧索引不能冒充经过DS签名和结算校验的权威证据清单。

### 6.2 API与单写切换

- GameData 新增 `POST /internal/replay-evidence/legacy-player-index`，要求用途隔离Bearer凭据和
  `Idempotency-Key=eventId`，拒绝敏感字段、错误分类、超限正文和冲突重放。
- PlayerData 保留 `/internal/sources/replays` 响应兼容，但仅调用GameData窄客户端，不透明重试POST，
  不再调用 `IPlayerDataStore.RecordEvidenceAsync`。
- `player_data.reject_replay_evidence_write` 触发器在数据库层拒绝任何遗留Replay INSERT/UPDATE；
  其他四类证据仍由PlayerData按后续独立步骤迁移。
- Redis无变化；结算、战绩查询和EdgeGateway响应无变化。

### 6.3 数据映射与迁移

| 源字段 | 目标字段 | 转换 |
|---|---|---|
| `event_id` | `event_id` | UUID原值，主键幂等 |
| `player_id` | `player_id` | 原值 |
| `occurred_at_utc` | `occurred_at` | UTC原值 |
| `source_reference` | `source_reference` | 原值，唯一约束 |
| `data` | `data` | JSONB原值，不复制到普通日志 |
| `sensitivity` | `sensitivity` | Replay只允许Restricted |
| 规范字段摘要 | `request_fingerprint` | SHA-256，仅用于冲突审计 |
| `recorded_at_utc` | `recorded_at` | 原值 |

迁移入口为 `sql/stage-8.2-replay-evidence-migration.sql`，使用 `INSERT ... ON CONFLICT DO NOTHING`
并在同一事务核对缺失行后关闭旧写入口。只读核验位于
`sql/stage-8.2-replay-evidence-validation.sql`。迁移只扫描Replay部分索引；生产执行前应通过
`EXPLAIN`与实际行数评估扫描时间，必要时先在线建立`evidence_type`部分索引。脚本不更新源行，
不会对钱包和奖励事务加写锁。

### 6.4 兼容与回滚

推荐顺序为：应用目标表 → 执行幂等迁移与核验 → 部署GameData → 部署PlayerData适配器 →
观察旧入口成功率和调用方 → 确认无旧Replay写入。紧急回滚必须先把流量和PlayerData镜像切回8.1，
再执行 `sql/stage-8.2-replay-evidence-rollback.sql` 删除旧写拒绝触发器。回滚不删除GameData记录，
避免证据丢失；禁止在新旧两个入口同时开放写入。

### 6.5 当前验收门

8.2完成后仍不能开始8.4或8.5；下一步只能单独实施8.3 Economy/Rewards/Inventory迁移。
真实生产数据数量、锁等待和旧接口调用量仍必须由授权迁移人员附入工单，本地测试不得读取生产凭据。

### 6.6 8.2技术验证结果

- `dotnet restore`、Release构建通过，0警告、0错误。
- 非外部全解决方案测试285项通过、4项NATS外部集成测试按Category跳过、0失败。
- GameData阶段8.2测试覆盖专用凭据、敏感字段拒绝、首次写入、重复写入和冲突重放。
- PlayerData兼容测试证明旧URL调用GameData适配器，且同EventId没有进入PlayerData投影Outbox。
- 架构测试证明GameData目标表、PlayerData旧写触发器和窄客户端边界存在。
- 一次性PostgreSQL 17验证迁移脚本重复执行、目标数量、旧写拒绝和回滚恢复，1项通过。
- Compose展开、Kubernetes YAML、阶段迁移YAML和两个服务的JSON配置语法通过。

技术结论：8.2代码和本地数据迁移验收通过。生产验收仍需在授权停写窗口执行数量核验、锁影响观察、
旧接口调用量确认并附入工单；未完成这些人工证据前不得删除PlayerData旧Replay行或开始8.3生产切流。
