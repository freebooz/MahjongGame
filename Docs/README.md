# 项目核心文档

本目录只保留可用于当前开发、运维、安全审查和人工验收的核心文档。阶段流水账、完成计划、
MCP 操作日志和截图报告不在工作树长期保存；需要追溯时使用 Git 历史和 CI/审查证据。

## 架构与功能

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
- [UI 资产与视觉核心规范](UI_ASSET_AND_VISUAL_STANDARD.md)

## 安全、可观测性与运维

- [PostgreSQL 最小权限与生产身份](POSTGRES_LEAST_PRIVILEGE_AND_PRODUCTION_IDENTITY.md)
- [结构化日志规范](OBSERVABILITY_LOGGING_STANDARD.md)
- [告警运行手册](RUNBOOKS/OBSERVABILITY_ALERTS.md)
- [多集群与 SLO 治理运行手册](RUNBOOKS/SLO_MULTI_CLUSTER_GOVERNANCE.md)
- [Linux、Docker 与 Kubernetes 部署](../Deploy/README.md)

服务专属说明保存在对应 `Services/GuiyangMahjong.*/README.md`，机器契约及版本规则保存在
`Contracts`。`architecture/stage-*.md` 只保留已经实施的迁移、兼容和回滚证据；当前行为必须以
`architecture/current-*.md` 和实际源码为准。
# 阶段 11 配置与多版本治理

- [配置中心、灰度发布与多版本治理](architecture/stage-11-configuration-release-governance.md)
