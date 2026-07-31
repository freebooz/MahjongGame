# 阶段 11：配置中心、功能开关、灰度发布与多版本治理

## 1. 阶段边界与实际实现

本阶段新增 `GuiyangMahjong.Configuration`，它是动态业务配置的唯一写入者。数据库连接、Redis/NATS 地址、端口、证书、工作负载身份和签名密钥仍属于静态启动配置，只能由环境变量或 Secret 注入。没有修改麻将规则、结算、玩家资产，也没有让 Admin 直接写其他服务业务表。

配置发布链路为：

```text
Angular 22
  → Admin BFF（管理员身份、MFA、RBAC）
  → Configuration Service（草稿、Schema/安全校验、异人审批）
  → PostgreSQL 不可变版本 + 当前指针 + 同事务 Outbox
  → configuration.published.v1
  → 服务拉取、验签、原子切换、应用回执
```

EdgeGateway 已实现实际 LKG 消费：启动先使用静态安全基线，随后恢复磁盘 LKG；只有配置键、Schema、正文 SHA-256 和 HMAC 签名全部有效且版本递增时才原子切换。配置中心不可用、签名错误或磁盘故障不会清空当前策略。动态功能默认关闭，回滚时可恢复静态最低版本和协议门禁。

## 2. 配置模型

`platform.runtime` v1 包含客户端最低/推荐/阻断版本、API 协议、功能开关、稳定灰度规则、Fleet 路由、房间模板和风险策略版本。递归安全扫描拒绝 password、secret、token、private key、certificate、connection string、signing key 等疑似敏感字段。

灰度按 `SHA256(identity + experiment_id)` 的前 64 位稳定映射到 `0..9999`。身份优先使用玩家 ID，未登录时使用不可逆设备摘要。白名单、测试账号、渠道、客户端版本、平台和地域先收窄候选，再执行万分比分桶。写请求只选择一个真实分组，不执行影子副作用。

## 3. 不可变 Build、RuleSet 与房间冻结

同名 `server_build` 不能绑定不同 OCI digest；同名 `ruleset_version` 不能绑定不同规则包 SHA-256。Stable 和 Canary Fleet 使用不同名称、标签和不可变镜像引用并可同时存在。房间创建/分配时已有的 Build、RuleSet、协议和 Room Epoch 继续冻结在分配记录与 DS 启动参数中，因此新发布只影响新房间。停止 Canary 时应将新分配路由置为 `stopNewAllocations` 或恢复 Stable 路由，不能强杀正常旧房间。

## 4. 数据所有权与发布事务

| Schema | 表 | 唯一写入者 | 说明 |
|---|---|---|---|
| `configuration` | `config_drafts` | Configuration | 草稿、验证、审批、revision CAS |
| `configuration` | `config_versions` | Configuration | 不可变发布历史；更新/删除/清空由触发器拒绝 |
| `configuration` | `config_current` | Configuration | 单配置键当前版本指针 |
| `configuration` | `config_application_reports` | Configuration | 服务应用结果，不含正本 |
| `configuration_integration` | `platform_outbox` | Configuration 追加、Workers 调度 | 与版本切换同事务 |

生产 `mahjong_configuration` 使用 `NOINHERIT` 登录身份并显式切换 `mahjong_configuration_rw`，无 DDL 权限。Admin 没有上述 Schema 写权限。迁移遵循 `Expand → Migrate → Contract`；本阶段只有 Expand，无破坏性 Contract。

## 5. 管理、审计与快速回滚

管理页面按独立面板降级并显示来源、最后成功时间、数据年龄和 60 秒陈旧阈值。`governance.publisher` 可创建和验证草稿；`governance.approver` 才能发布，且审批人不得等于创建人。浏览器只持有 HttpOnly BFF 会话，不获得 Configuration 命令 Token。

正常回滚不会覆盖旧版本，而是复制已验真的历史正本并创建更高的新版本，记录 `rollback_of_version`、操作人、审批人、工单、TraceId 和幂等键。紧急应用回滚可关闭动态配置开关，使 EdgeGateway 继续使用 LKG/静态基线；数据库不可变历史保留用于调查。

## 6. CI/CD 与供应链

Services CI 覆盖 .NET、Angular、契约门禁、Docker 构建矩阵、SBOM、Trivy 镜像扫描、Compose 和 Helm 渲染。Unreal CI 继续分别构建 Client/Server。`Signed Canary Release` 仅接受带 SHA-256 digest 的镜像，受 GitHub Environment 人工审批，并用 Cosign keyless 身份签名。Stable/Canary 和配置版本是显式发布输入；回滚动作只恢复旧 Fleet 新分配流量。

## 7. 可观测性

配置服务输出 `mahjong.configuration.current.version`、发布计数和应用结果计数。发布和应用日志为结构化日志，不能包含配置正文、Token 或签名密钥。部署和 DS 指标可使用 `service_version`、`client_version`、`protocol_version`、`server_build`、`ruleset_version`、`config_version`、`fleet`、`region`、`cell`、`canary_group`；禁止把玩家 ID 或房间 ID 作为 Prometheus 标签。

## 8. 当前兼容与明确限制

- 旧 API 响应没有被删除或修改；Configuration 与 Edge 动态消费均有关闭开关。
- UE Dedicated Server 的游戏 UDP 路径仍不经过 HTTP 网关。
- 当前签名使用部署 Secret 注入的 HMAC，适合增量阶段；生产深化建议迁移为 KMS/HSM 托管的 Ed25519 非对称签名，让消费者只持公钥。
- Allocation 通过签名 FleetRoute LKG 将已冻结的 Build/RuleSet/Protocol/Region 解析为唯一 Fleet；停止路由只拒绝新分配，配置中心不可用或验签失败时继续使用最近有效路由。Lobby 仍负责在创建房间时选择业务版本，Allocator 只执行并校验已选版本，避免承担灰度人群决策。
- RuleSet 包校验与 DS 运行时加载仍由构建/发布链冻结，本阶段不允许运行中覆盖规则包。

## 9. 回滚步骤

1. 停止新 Canary 分配，恢复最近 Stable Fleet 路由。
2. 关闭 `Admin__Configuration__Enabled`，阻止新草稿发布。
3. 关闭 `EdgeGateway__DynamicConfiguration__Enabled` 或发布历史正本的新回滚版本。
4. 不终止已运行旧房间；等待自然结算。
5. 保留 `configuration` Schema 与 Outbox；修复后可继续发布尚未发送的事件。

## 10. 验收测试映射

`ConfigurationGovernanceTests` 覆盖 Schema 错误、敏感字段、稳定分桶、Canary 隔离、Build/RuleSet 不可覆盖、异人审批、幂等、签名、客户端兼容和回滚。EdgeGateway 现有契约测试与全解决方案测试用于确认旧入口兼容。部署验证入口为 `Scripts/Test-ReleaseGovernance.ps1`、Compose config、Kubernetes YAML 解析和 Helm lint/template。

## 11. 本阶段实际验证记录

- `dotnet restore`：通过。
- `dotnet build -c Release --no-restore`：通过，0 警告、0 错误。
- `dotnet test -c Release --no-build --filter "Category!=ExternalPersistence"`：281 项通过；4 项 NATS/外部集成用例按 Category 明确跳过，并非失败。
- Configuration PostgreSQL 外部持久化测试：1 项通过，验证 Schema 可重复应用、发布与 Outbox 同事务以及不可变触发器。
- Angular：TypeScript/lint 通过，3 项测试通过，生产构建通过；`npm audit --audit-level=high` 通过高风险门禁，仍报告 3 个 Angular CLI 工具链的 moderate 间接依赖问题，未使用 `--force` 引入破坏性降级。
- 发布与部署：Release Governance 门禁、Compose 配置、Kubernetes/Agones YAML 解析、Helm lint/template 均通过。
- Unreal：本阶段未修改 Target、Build.cs、C++ 或资源；Client/Server 的独立构建仍由既有 UE CI 承担，本地未重复执行完整 UE 产物构建。
