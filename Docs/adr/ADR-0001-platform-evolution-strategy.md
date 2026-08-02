# ADR-0001：平台增量演进策略

- 状态：Accepted
- 日期：2026-07-31
- 决策范围：贵阳麻将平台架构升级
- 关联基线：`Docs/architecture/current-*.md`

## 背景

项目已经具备 UE Client/Dedicated Server、多个 .NET 服务、Angular Admin、PostgreSQL、Redis、Kubernetes/Agones 和完整可观测栈。现有实现中 Auth、Lobby、Allocator、PlayerData、Admin 已有清晰但尚不完全成熟的数据边界；实时牌局权威在 DS，控制面和监控能力已经投入较多代码。

若直接推倒重建或一次性拆成大量微服务，将同时改变客户端入口、实时网络、数据所有权、部署和运维流程，无法建立可验证的兼容迁移路径。当前工作区还包含未提交的公平性升级，更需要先冻结事实基线。

## 决策

采用“基线先行、契约驱动、模块化单体优先、按所有权增量切换”的演进策略。

1. 每次只实施一个明确阶段；阶段结束时保持可编译、可测试、可运行。
2. 任何改动前先检查实际仓库，不根据目录名推断实现。
3. 保留现有服务和 API 入口，通过版本化兼容适配逐步演进，不批量重命名或移动。
4. 客户端 HTTP 最终统一经 EdgeGateway，但 UE Client 与 DS 的 Unreal 实时网络永不经 HTTP 网关。
5. DS 始终是实时牌局权威；客户端只提交意图。
6. Room 控制面与实时牌局状态明确分离；Lobby 当前兼有结算持久化的事实先记录，后续通过兼容契约迁移到 Settlement 所有权。
7. PostgreSQL 是业务权威；Redis 只承担缓存、会话、路由、租约、限流和可恢复临时状态。
8. 跨服务关键写入逐步采用本地事务 + Outbox，消费端采用 Inbox/业务唯一约束。
9. Admin/Angular 不直接修改其他所有者的业务表；高风险管理操作必须二次确认、独立审批、RBAC/ABAC、脱敏和完整审计。
10. 所有关键调用最终统一传播 `request_id`、`correlation_id`、`trace_id`，且禁止记录凭据、支付签名和私有手牌。
11. 数据库变更必须有版本化 migration、所有权、升级/回滚和数据校验；生产运行身份不具备 DDL。
12. 发布以自动化基线为门禁：.NET、Angular、Compose/Kubernetes/Agones、UE Target/Build.cs、Client/Server 构建和适用的端到端流程。

## 阶段 0 的具体决定

- 只建立当前实现、接口、数据、Redis、运行依赖、主流程和风险基线。
- 不新增 EdgeGateway、NATS、Settlement 服务或任何业务模块。
- 不修改 API 响应、数据库所有权、Redis 键、麻将规则和目录结构。
- 当前脏工作区作为事实记录，不视为干净版本标签。

## 兼容策略

1. 新 HTTP 能力优先新增版本化路径或兼容字段，旧调用方在迁移窗口继续工作。
2. 不对 POST、资产、奖励、结算和 GM 命令做无条件透明重试；调用方提供稳定幂等键。
3. 数据迁移采用先扩展、双读/影子验证、单写切换、再收缩；禁止长期双写。
4. 事件契约先发布 schema/version 和消费者兼容性，再切换生产者。
5. UE 网络协议变更必须同时验证 Client/Server Target，并保留协议版本拒绝和升级提示。

## 数据所有权原则

| 域 | 当前所有者 | 目标演进原则 |
|---|---|---|
| Identity/Session | Auth | 保持单写，Gateway 只代理 |
| Room control plane | Lobby | 保持控制面单写 |
| Realtime match | DS | 进程内权威，所有外部命令是意图 |
| Allocation/lifecycle | Allocator/Agones | 统一实例状态机，不写牌局结果 |
| Wallet/reward/evidence source | PlayerData | 资产命令幂等、双人审批 |
| Admin workflow/audit | Admin | 只写自有审批/投影/审计 |
| Final settlement | 当前 Lobby，目标 Settlement | 通过版本化命令和 Outbox 迁移，不长期双写 |

## 可观测与安全原则

- 结构化日志默认拒绝敏感字段；
- 高基数业务 ID 进入 Trace/日志，不进入无界指标标签；
- 监控聚合必须显示来源、新鲜度和降级状态；
- 高风险操作使用新鲜权威快照和 expected version/state；
- break-glass 必须 MFA、短时、理由和额外审计；
- 生产凭据只从 Secret/密钥服务注入，不进入仓库、镜像、命令日志或文档。

## 备选方案

### 一次性微服务重写

拒绝。迁移面过大，无法在一个阶段内同时证明客户端、DS、数据和管理链路兼容。

### 维持当前结构不做所有权治理

拒绝。当前 Redis 安全状态、Lobby 结算职责、迁移回滚和跨服务关联已形成明确风险。

### 先引入消息总线再整理契约

拒绝。消息基础设施不能替代所有权、幂等和事务边界；应先冻结同步契约和数据责任。

## 影响

正面：

- 每个阶段有可回滚、可验证的边界；
- 复用现有大量测试、监控和管理实现；
- 降低客户端与实时牌局同时迁移的风险；
- 数据所有权和审计责任可逐步收敛。

代价：

- 迁移期需要兼容适配和重复验证；
- 某些当前耦合会暂时保留；
- 每阶段必须维护文档、契约、迁移和回滚证据。

## 回滚

ADR 本身不改变运行时。若后续证明策略不适用，应新增 ADR 标记本 ADR 为 Superseded，而不是改写历史。阶段 0 文档可单独删除，不影响代码和数据。

