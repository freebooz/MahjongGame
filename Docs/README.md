# 项目核心文档

本目录只保留可用于当前开发、运维、安全审查和人工验收的核心文档。阶段流水账、完成计划、
MCP 操作日志和截图报告不在工作树长期保存；需要追溯时使用 Git 历史和 CI/审查证据。

## 架构与功能

- [完整应用架构](FULL_APPLICATION_ARCHITECTURE.md)
- [解决方案项目架构及目录结构](SOLUTION_PROJECT_ARCHITECTURE_AND_DIRECTORY_STRUCTURE_20260731.md)
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
`Contracts`。文档若不再描述当前行为，应直接更新核心文档；不得新增按阶段编号的状态报告。
