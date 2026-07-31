# 当前 PostgreSQL 数据所有权

> 盘点日期：2026-07-31。依据各服务 `Storage/schema.sql`、存储实现和 `Deploy/postgres/least-privilege` SQL。

## 1. Schema 与迁移方式

| Schema | 业务所有者 | 当前迁移源 | 运行时写入者 |
|---|---|---|---|
| `public` 中的 `auth_*` | Auth | `GuiyangMahjong.Auth/Storage/schema.sql` | Auth |
| `public` 中的 `lobby_*`、房间/比赛历史表 | Lobby | `GuiyangMahjong.Lobby/Storage/schema.sql` | Lobby |
| `player_data` | PlayerData | `GuiyangMahjong.PlayerData/Storage/schema.sql` | PlayerData |
| `admin_monitor` | Admin | `GuiyangMahjong.Admin/Storage/schema.sql` | Admin |

每个 Schema 文件被构建到唯一的 `Schemas/{Service}/schema.sql`，不会因同名文件互相覆盖。生产运行时校验禁止 Auth、Lobby、PlayerData、Admin 自动执行迁移；`mahjong_migration` 独立承担 DDL。

当前没有迁移版本表、按版本排序的 up/down 脚本，也没有自动回滚实现。Schema 脚本适合幂等初始化/前向补丁，但不足以证明任意版本可安全升级和回退。

## 2. 表、主键与主要约束

### 2.1 Auth 所有表（`public`）

| 表 | 主键 | 唯一/索引 | 外键与约束 |
|---|---|---|---|
| `auth_identities` | `installation_hash` | `player_id` UNIQUE；`(created_at_utc DESC, player_id DESC)` | 无 |
| `auth_refresh_sessions` | `session_id` | `(player_id, expires_at_utc DESC)` | `player_id → auth_identities(player_id) ON DELETE CASCADE` |
| `auth_login_events` | `event_id` | 玩家时间、设备时间索引 | `player_id → auth_identities` |
| `auth_admin_commands` | `command_id` | 主键即管理幂等键 | 无 |
| `auth_player_controls` | `player_id` | 版本、状态和冻结有效性 CHECK | `player_id → auth_identities` |
| `auth_player_control_events` | `command_id` | `(player_id, effective_at_utc DESC)` | `player_id → auth_identities`；保存前后状态、审批人、TraceId、工单 |

### 2.2 Lobby 所有表（`public`）

| 表 | 主键 | 唯一/索引 | 外键与约束 |
|---|---|---|---|
| `lobby_rooms` | `room_id` | `room_code` UNIQUE；监控游标、生命周期、玩家 JSONB GIN 索引 | 权威房间快照和 `state_sequence` |
| `active_player_rooms` | `player_id` | `room_id` 索引 | `room_id → lobby_rooms ON DELETE CASCADE`；保证玩家只有一个活动房间 |
| `match_results` | `(match_id, result_sequence)` | 房间/时间索引 | 结果序列承担幂等与冲突检测 |
| `room_event_history` | `event_id` | 房间游标、match 时间索引 | `room_id → lobby_rooms`；触发器禁止 UPDATE/DELETE/TRUNCATE |
| `player_room_history` | `(player_id, room_id, joined_at_utc)` | 玩家游标索引 | `room_id → lobby_rooms`；由房间快照触发器投影 |
| `player_connection_history` | `event_id` | 玩家连接游标索引 | `event_id → room_event_history`；由事件触发器投影 |

### 2.3 PlayerData 所有表（`player_data`）

| 表 | 主键 | 唯一/索引 | 外键与约束 |
|---|---|---|---|
| `wallet_balances` | `(player_id, asset_code)` | 主键 | 余额非负、版本号 |
| `reward_grants` | `reward_grant_id` | 玩家/领取时间索引 | 金额为正，状态仅 Claimed/Revoked |
| `wallet_transactions` | `transaction_id` | `command_id` UNIQUE；玩家时间索引 | 双人审批；操作类型受限 |
| `evidence_events` | `event_id` | `(evidence_type, source_reference)` UNIQUE；玩家/类型/时间索引 | 类型和敏感级别 CHECK |
| `projection_outbox` | `event_id` | 状态/可用时间索引 | `event_id → evidence_events`；租约与重试状态 |

### 2.4 Admin 所有表（`admin_monitor`）

| 表 | 主键 | 主要唯一/索引 | 外键与约束 |
|---|---|---|---|
| `action_requests` | `action_request_id` | 状态/请求时间索引；业务幂等字段 | 保存确认、理由、工单、TraceId、预期状态 |
| `action_approvals` | `approval_id` | `(action_request_id, approved_by)` UNIQUE | `action_request_id → action_requests` |
| `audit_ledger` | `audit_id` | 目标/时间索引 | 哈希链、前后状态、审批记录 |
| `audit_archive_outbox` | `audit_id` | 分发索引 | `audit_id → audit_ledger` |
| `command_outbox` | `outbox_id` | 分发索引；每 action 唯一 | `action_request_id → action_requests` |
| `management_cases` | `case_id` | 目标/时间索引；来源命令约束 | `action_request_id → action_requests` |
| `player_asset_operations` | `operation_id` | 玩家/时间索引 | 指向 action 和 case |
| `player_evidence` | `event_id` | `(evidence_type, source_reference)` UNIQUE；玩家索引 | 仅投影证据 |
| `player_chat_access_grants` | `grant_id` | 玩家/有效期查询索引 | 范围、理由、审批、TraceId |
| `admin_sessions` | `session_hash` | 未撤销会话到期索引 | 仅保存会话/CSRF/设备/IP 摘要和授权快照，不保存企业 Token |
| `admin_login_security_events` | `event_id` | 操作者/时间索引 | 追加式登录成功、失败及异常设备/IP 证据；禁止更新、删除和清空 |

## 3. 数据所有权矩阵

符号：`W` 写入，`R` 读取，`C` 通过所有者 API 发命令，`P` 通过 Outbox 投影，`-` 无直接访问。

| 数据域 | Auth | Lobby | Allocator | PlayerData | Admin | DS |
|---|---:|---:|---:|---:|---:|---:|
| `auth_*` | W/R | - | - | C（账号策略查询） | R/C（HTTP） | - |
| `lobby_rooms` / `active_player_rooms` | - | W/R | - | - | R/C（HTTP） | C（注册/心跳） |
| `match_results` | - | W/R | - | - | R（HTTP） | C（结算 HTTP） |
| 房间/玩家历史 | - | W/R | - | - | R（HTTP） | C（心跳事件） |
| `player_data.*` | - | - | - | W/R | R/C（HTTP） | - |
| `admin_monitor.*` | - | - | - | P（HTTP Outbox） | W/R | - |
| Allocator JSON 状态/结算恢复目录 | - | C | W/R | - | R/C（HTTP） | C |

结论：

- 未发现两个服务长期直接双写同一 PostgreSQL 业务表。
- Admin 不直接写 Auth、Lobby、PlayerData 的业务表；管理操作先写 Admin 自有审批/Outbox，再调用所有者 API。
- Dedicated Server 没有 Npgsql 或 PostgreSQL 连接，不直接写任何数据库表。
- PlayerData 向 Admin 的证据同步采用本地事务 + `projection_outbox`；Admin 以 `event_id`/来源唯一约束幂等接收。
- Admin 命令采用本地审批事务 + `command_outbox`，但 Lobby 的结算链路当前仍由 Lobby 自身持久化，并未形成独立 Settlement 模块。

## 4. 生产身份与权限

`Deploy/postgres/least-privilege` 定义：

| 登录身份 | 激活角色 | 设计权限 |
|---|---|---|
| `mahjong_auth` | `mahjong_auth_rw` | 仅 Auth 表读写 |
| `mahjong_lobby` | `mahjong_lobby_rw` | 房间/活动租约/结果读写；事件只追加；历史只读 |
| `mahjong_player_data` | `mahjong_player_data_rw` | `player_data` 表读写 |
| `mahjong_admin` | `mahjong_admin_rw` | Admin 自有表读写；审计账本限制为读取/追加 |
| `mahjong_monitor` | `mahjong_monitor_ro` | 跨域只读监控 |
| `mahjong_audit_writer` | `mahjong_audit_append` | 审计追加 |
| `mahjong_archive` | `mahjong_archive_dispatch` | 审计归档 Outbox 分发 |
| 独立迁移身份 | `mahjong_migration` | DDL、Schema 所有权 |

登录身份使用 `NOINHERIT`，连接需显式 `SET ROLE`/连接参数激活单一权限角色。角色均为非超级用户、不可建库、不可建角色、不可复制。`public` Schema 的公共 CREATE 被撤销。

只要生产连接串使用上述运行身份，生产服务不具备 DDL 权限；代码同时在 Production 环境拒绝 `ApplyDatabaseMigrations=true`。风险在于仓库无法证明外部实际部署的连接串一定使用这些身份，因此发布门禁必须执行最小权限验证脚本。

## 5. 数据写入与幂等边界

| 写入 | 原子/幂等机制 | 当前风险 |
|---|---|---|
| Guest 身份创建 | installation hash 主键、player_id UNIQUE | 设备身份依赖 pepper 安全 |
| Refresh 轮换 | PostgreSQL 事务、旧会话单次撤销 | 仅数据库模式可跨实例保证 |
| 房间创建/加入 | 房间号 UNIQUE、玩家活动租约主键、状态序列 | `payload` JSONB 承载大量状态，迁移/查询复杂 |
| 结算 | `(match_id,result_sequence)` 主键 + payload 冲突比较 | Lobby 同时承担控制面与结算持久化 |
| 房间事件 | `event_id` 主键、追加触发器 | 事件和业务快照不是通用 Outbox 同一事务 |
| 钱包命令 | `command_id` UNIQUE、余额行锁/版本、双人审批 | 需持续验证撤销奖励的业务关联 |
| Admin 命令 | action + approval + command Outbox | 下游多服务部分成功时依赖命令状态机补偿 |

## 6. 迁移、回滚与验证

- 迁移生成：当前为人工维护 Schema SQL，不是自动生成。
- 升级：独立迁移 Job 顺序应用 Auth、Lobby、PlayerData、Admin Schema 及权限 SQL。
- 回滚：阶段10新增 `rollback-stage10.sql` 可在先关闭 BFF 会话后精确删除管理员会话和登录安全事件表；其他历史 Schema 仍采用发布前备份或向前修复。
- 数据校验：有架构测试验证 Schema 输出隔离；外部 PostgreSQL 测试验证关键唯一约束和事务。
- 阶段10已在临时 PostgreSQL 17 上验证 Admin Schema 前滚、并发持久化和精确回滚；生产仍必须由独立 migration 身份执行。
# 阶段 11 增量：Configuration 所有权

`configuration.config_drafts`、`configuration.config_versions`、`configuration.config_current`、`configuration.config_application_reports` 和 `configuration_integration.platform_outbox` 由 `GuiyangMahjong.Configuration` 唯一写入。Workers 仅可领取、标记和归档 Outbox；Admin 只能通过 Configuration 管理命令 API 操作，不能直接写表。已发布版本为不可变历史，回滚创建更高版本。

# 阶段 8.2 增量：ReplayEvidence 单写切换

`replay.evidence_manifests` 与 `replay.legacy_player_evidence` 由GameData唯一写入。PlayerData旧
`/internal/sources/replays` 仅为兼容转发入口，数据库触发器拒绝向
`player_data.evidence_events` 新增或更新Replay类型；旧行保留用于迁移核验和回滚，不形成长期双写。
Report、PaymentOrder、RewardClaim和AssetChange仍由PlayerData持有，等待8.3～8.5分别迁移。
