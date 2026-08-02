# 阶段 3：IdentityApp 增量升级实施与验收报告

## 1. 阶段范围

本阶段在保留 `GuiyangMahjong.Auth` 单一部署单元、现有外部 API 和现有两段式 HMAC Access Token 外层格式的前提下，建立：

- `Auth`：游客身份、身份解析和 Access Token 签发；
- `Sessions`：Refresh Token、Token Family、轮换、重用检测、撤销和会话并发策略；
- `Players`：玩家长期档案和内部只读目录；
- `Devices`：脱敏设备摘要、最后使用时间和设备切换事实；
- `Administration`：会话撤销、封禁、解封、禁言、风险标签和管理审计；
- `Infrastructure`：PostgreSQL、存储生命周期、密钥配置和集成 Outbox。

本阶段没有拆分部署服务，没有修改 Lobby 房间状态机、Dedicated Server、麻将规则、结算、资产或奖励流程，也没有引入正式消息总线。

## 2. 实施前真实状态

- 生产接口实际为 `POST /v1/auth/guest`、`POST /v1/auth/refresh` 和 `POST /v1/auth/logout`。
- 仓库不存在独立的公开玩家资料 API；昵称位于身份表，Admin/PlayerData 使用内部玩家监控详情。
- `AuthService` 同时协调身份、会话、登录设备事实；统一 `IAuthStore` 暴露全部存储能力。
- Refresh Token 已随机生成且只持久化 SHA-256 摘要，但没有 Token Family、重用处置或 Epoch。
- Access Token 是 `base64url(json).base64url(hmac-sha256)` 两段式兼容契约；Lobby 和 EdgeGateway 分别本地验签。
- Auth 表全部位于 `public` Schema。
- 未发现 Auth 直接读写 Room、Game、Settlement、Inventory、奖励、聊天内容或 Dedicated Server 生命周期表。

## 3. 模块边界

`IAuthStore` 继续作为阶段 3 之前调用方的兼容聚合适配器，新增代码使用以下窄端口：

| 模块 | 端口 | 权限边界 |
| --- | --- | --- |
| Auth | `IIdentityRepository` | 只取得或创建身份 |
| Sessions | `ISessionRepository` | 只创建、轮换、撤销会话 |
| Players | `IPlayerProfileReader`、`IPlayerDirectoryReader` | 只读取非凭证档案和脱敏目录 |
| Devices | `IDeviceAuditWriter` | 只追加脱敏登录和设备信号 |
| Administration | `IIdentityAdministrationStore` | 只执行 Identity 范围内的账号控制 |
| Infrastructure | `IIdentityStorageLifecycle` | 初始化或检查存储，不决定业务策略 |

架构测试会拒绝 Auth 项目拥有 Lobby 房间表，也会拒绝 Players 模块引用签名密钥、Token 签发器、Refresh Session 或 Auth 安全配置。

## 4. Token 与会话安全

### 4.1 Access Token

- 默认有效期仍为 15 分钟，外层格式不变。
- 实际登录令牌增加 `Sid`、`SessionEpoch` 和 `SecurityEpoch` 声明；旧验证器会忽略新增字段。
- 原 v1 确定性测试向量和旧签发重载保留，避免破坏既有契约测试。
- Auth 增加受内部只读凭证保护的 `GET /internal/identity/token-validation-config`，只发布算法、格式、KeyId、TTL 和声明名，不发布 HMAC 密钥。
- Lobby 和 EdgeGateway 支持当前密钥加多个旧验证密钥的有界重叠窗口。

### 4.2 Refresh Token

- 每次首次登录创建唯一 `family_id`。
- 轮换后的会话继承 Family、设备、`session_epoch` 和 `security_epoch`，并记录父子会话。
- 旧 Refresh Token 首次被正确轮换后标记为 `Rotated`。
- 正确旧 Token 再次出现时判定为重用：同一事务撤销整个 Family，记录重用时间，并同时推进两个 Epoch。
- 登出、管理强制下线、封禁和冻结记录稳定撤销原因。
- 管理全量撤销推进 `session_epoch`；凭证泄漏、封禁和冻结同时推进 `security_epoch`。

### 4.3 并发策略

配置节 `Sessions`：

```json
{
  "Mode": "MultiDevice",
  "MaximumActiveSessions": 4
}
```

可通过 `Sessions__Mode` 和 `Sessions__MaximumActiveSessions` 覆盖。`SingleDevice` 在新会话创建事务内撤销全部旧会话；`MultiDevice` 按最早创建时间淘汰超出上限的会话。

## 5. 数据库变化与所有权

| Schema | 表 | 所属模块 | 运行时权限 |
| --- | --- | --- | --- |
| `auth` | `auth_identities` | Auth | Auth 读写 |
| `auth` | `auth_admin_commands`、`auth_player_controls`、`auth_player_control_events` | Administration | Auth 读写 |
| `session` | `auth_refresh_sessions` | Sessions | Auth 读写 |
| `player` | `player_profiles` | Players | Auth 读写 |
| `integration` | `auth_login_events`、`auth_devices`、`auth_device_switch_events` | Devices | Auth 读写 |
| `integration` | `identity_outbox` | Infrastructure/Integration | Auth 读写 |

`auth_identities.display_name` 暂时作为旧 API 的兼容快照保留，长期档案权威表为 `player.player_profiles`。本阶段不建立双向双写入口。

Schema 脚本会使用 `ALTER TABLE ... SET SCHEMA` 原位迁移旧表，保留数据、索引和外键；既有会话使用自身 SessionId 补齐单成员 Family。生产运行账号仍无 DDL 权限，DDL 由 `mahjong_migration` 执行。

升级：

```bash
psql -v ON_ERROR_STOP=1 -f Services/GuiyangMahjong.Auth/Migrations/0003_identity_app_modules.up.sql
```

回滚：

```bash
psql -v ON_ERROR_STOP=1 -f Services/GuiyangMahjong.Auth/Migrations/0003_identity_app_modules.down.sql
```

回滚会删除新玩家档案、设备切换和未发布 Outbox 表，执行前必须备份；旧身份和会话数据会迁回 `public`。

## 6. 事件变化

会话撤销与业务事务同事务写入 `integration.identity_outbox`，事件类型使用阶段 2 契约：

- `event_type = session.revoked`
- `schema_version = 1`
- Payload：`session_id`、`player_id`、`reason_code`、`revoked_at`

Payload 不包含 Token、Token 哈希、完整 IP、原始设备指纹或私有游戏数据。本阶段按约束只落本地 Outbox，不接入正式 NATS；消息派发和消费端 Epoch 水位缓存属于后续阶段。

## 7. API 与调用方兼容

- `/v1/auth/guest`、`/v1/auth/refresh`、`/v1/auth/logout` 的路径和响应结构未改变。
- Admin 会话撤销、账号控制和玩家监控路径未改变。
- 新增内部只读验证元数据接口，不影响旧调用方。
- EdgeGateway 路由未改变，UE 客户端无需修改。
- Lobby 和 EdgeGateway 在旧密钥重叠窗口内可验证轮换前签发且未过期的 Token。
- 没有新增公开玩家档案接口，因为实施前不存在该接口；内部详情兼容保留。

## 8. 配置

| 环境变量 | 默认值 | 说明 |
| --- | --- | --- |
| `Auth__ActiveSigningKeyId` | `primary` | 非敏感轮换标识 |
| `Sessions__Mode` | `MultiDevice` | `SingleDevice` 或 `MultiDevice` |
| `Sessions__MaximumActiveSessions` | `4` | 多端活跃会话上限，1 到 32 |
| `Lobby__PreviousTokenValidationKeys__0` | 空 | 第一把旧 Token 验证密钥 |
| `EdgeGateway__PlayerTokens__PreviousLegacyValidationKeys__0` | 空 | 网关第一把旧验证密钥 |

轮换顺序：先向 Lobby/EdgeGateway 加入旧验证密钥，再切换 Auth 当前签名密钥；等待最长 Access Token 生命周期加时钟偏差后移除旧密钥。任何密钥都不得写入日志或文档。

## 9. Redis 变化

本阶段没有新增或修改 Redis Key。Session、Epoch 和 Token Family 的权威状态全部位于 PostgreSQL；没有使用 Redis 锁作为正确性保证。

## 10. 验证结果

- `dotnet restore`：25 个项目成功。
- `dotnet build`：25 个项目成功，0 警告，0 错误。
- `dotnet test`：204 项通过，22 项按外部依赖条件跳过，0 失败。
- Auth 外部 PostgreSQL 测试：4 项全部通过。
- 独立 PostgreSQL 17 容器中完成升级、回滚、再次升级；前后身份记录数量一致。
- 最小权限脚本在完整服务 Schema 上执行成功；验证 Auth 运行角色可读写 Auth Schema，但不能读取 Lobby 业务表。
- `Deploy/linux/compose.yaml` 与 `Deploy/docker-compose.yml` 配置校验通过，校验过程禁用 `.env` 自动读取并只使用临时占位值。
- Kubernetes 目录 9 个 YAML、27 个文档完成离线语法和基础字段校验。由于本机没有可用集群，`kubectl` 服务端发现校验未执行。
- 本阶段未修改 Angular 或 Unreal Engine，因此未重复执行 Angular、Client、Server 或 UE 自动化构建。

## 11. 已知限制与下一阶段前置条件

- HMAC 没有可公开验证公钥；当前仍由部署密钥系统分别向 Auth、Lobby 和 EdgeGateway 注入共享密钥。后续若改为非对称签名，应提供 JWKS 和 `kid`，但不能在本阶段破坏旧格式。
- Epoch 已写入 Token、身份表和撤销 Outbox；正式消息总线与各消费者的实时 Epoch 水位缓存尚未接入。
- `player_profiles` 已建立并在游客身份创建事务中初始化，但公开玩家资料维护 API 不在本阶段范围内。
- 旧 `Domain`、`Services`、`Storage` 目录仍作为渐进兼容层存在；新增调用必须使用六模块窄端口，后续可逐文件迁移，禁止一次性批量移动。

阶段 3 验收结论：外部接口兼容、单部署保持、模块职责与数据所有权明确、会话安全和迁移回滚可执行，满足进入下一阶段的技术条件。
