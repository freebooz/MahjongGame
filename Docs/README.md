# 贵阳麻将平台现行文档

本索引只指向扩展升级后仍有效的架构、数据迁移、验收和运维文档。阶段 0 冻结的旧架构盘点，
以及仍以 Auth/Lobby/Allocator/PlayerData 旧边界为主的总览和监控设计已从工作树删除；如需审计历史，
使用 Git 记录，不得将旧文档当作现行实施依据。

## 平台演进与现行架构

<<<<<<< HEAD
- [当前系统与模块清单](architecture/current-system-inventory.md)
- [当前主业务流程](architecture/current-main-flows.md)
- [当前 API 清单](architecture/current-api-inventory.md)
- [当前运行时依赖](architecture/current-runtime-dependencies.md)
- [当前数据所有权](architecture/current-data-ownership.md)
- [当前 Redis 清单](architecture/current-redis-inventory.md)
- [当前风险与验证基线](architecture/current-risk-register.md)
- [当前严格配置与工具数据字典](architecture/current-configuration-inventory.md)
- [平台演进 ADR](adr/ADR-0001-platform-evolution-strategy.md)
- [服务器、房间与玩家实时监控](REALTIME_SERVER_PLAYER_MONITORING_REVIEW_20260728.md)
- [玩家监控管理与安全设计](PLAYER_MONITORING_ADMIN_DESIGN.md)
=======
- [ADR-0001：平台增量演进策略](adr/ADR-0001-platform-evolution-strategy.md)
- [阶段 1：Player EdgeGateway 统一接入](architecture/stage-1-edge-gateway.md)
- [阶段 2：Contracts 与 BuildingBlocks](architecture/stage-2-contracts-building-blocks.md)
- [阶段 3：IdentityApp](architecture/stage-3-identity-app.md)
- [阶段 4：LobbyControlApp](architecture/stage-4-lobby-control-app.md)
- [阶段 5：Allocation Service](architecture/stage-5-allocation-service.md)
- [阶段 6：Dedicated Server 生命周期与恢复](architecture/stage-6-dedicated-server-lifecycle-recovery.md)
- [阶段 7：GameData 与可信结算](architecture/stage-7-game-data-settlement.md)
- [阶段 8：PlayerData 职责拆解](architecture/stage-8-player-data-decomposition.md)
- [阶段 9：NATS JetStream 与 Workers](architecture/stage-9-nats-jetstream-workers.md)
- [阶段 10：Admin、TrustSafety 与监控](architecture/stage-10-admin-trust-safety-monitoring.md)
- [阶段 11：配置、灰度与多版本治理](architecture/stage-11-configuration-release-governance.md)

## 数据迁移与退役

- [阶段 8.3：Economy 迁移](architecture/stage-8.3-economy-migration.md)
- [阶段 8.4：Community/Chat 迁移](architecture/stage-8.4-community-chat-migration.md)
- [阶段 8.5：Backoffice 读模型迁移](architecture/stage-8.5-backoffice-read-model-migration.md)
- [阶段 8.6：PlayerData 退役](architecture/stage-8.6-player-data-decommission.md)
- 迁移、回滚与校验 SQL 保存在 [`architecture/sql`](architecture/sql)。

## Unreal Engine 与产品规范

- [Unreal Engine 5.8 工具链恢复与验收](architecture/ue58-toolchain-recovery-validation-20260731.md)
>>>>>>> ff4853bbd5831cc9697d440b00feb887168a2425
- [UI 资产与视觉核心规范](UI_ASSET_AND_VISUAL_STANDARD.md)

## 安全、可观测性与运维

- [PostgreSQL 最小权限与生产身份](POSTGRES_LEAST_PRIVILEGE_AND_PRODUCTION_IDENTITY.md)
- [结构化日志规范](OBSERVABILITY_LOGGING_STANDARD.md)
- [告警运行手册](RUNBOOKS/OBSERVABILITY_ALERTS.md)
- [多集群与 SLO 治理运行手册](RUNBOOKS/SLO_MULTI_CLUSTER_GOVERNANCE.md)
- [Linux、Docker 与 Kubernetes 部署](../Deploy/README.md)

<<<<<<< HEAD
服务专属说明保存在对应 `Services/GuiyangMahjong.*/README.md`，机器契约及版本规则保存在
`Contracts`。`architecture/stage-*.md` 只保留已经实施的迁移、兼容和回滚证据；当前行为必须以
`architecture/current-*.md` 和实际源码为准。
# 阶段 11 配置与多版本治理

- [配置中心、灰度发布与多版本治理](architecture/stage-11-configuration-release-governance.md)
=======
服务专属说明保存在对应 `Services/**/README.md`，机器契约及版本规则以 `Services/Contracts`
和实际项目文件为准。变更现行边界时，必须同步更新对应阶段文档、迁移说明和本索引。
>>>>>>> ff4853bbd5831cc9697d440b00feb887168a2425
