# 多集群、SLO 与治理运行手册

## 适用范围

本手册覆盖 Admin 聚合层、Lobby/Allocator 监控来源、Dedicated Server 遥测、管理审批、WORM/SIEM 审计归档和企业身份治理。生产变更必须关联工单，执行人员与审批人员职责分离。

## 服务目标与发布门禁

| SLI | 目标 | 窗口 | 责任人 | 发布门禁 |
|---|---:|---:|---|---|
| Admin API 可用性 | ≥ 99.9% | 滚动 30 天 | Admin 平台 | 预算耗尽或 1h 燃烧率 > 14.4 时阻断 |
| 遥测新鲜度 P95 | ≤ 10 秒 | 5 分钟 | 游戏服务端 | 超标 10 分钟阻断 |
| 审批到执行开始 P95 | ≤ 5 秒 | 5 分钟 | 管理工作流 | 超标 10 分钟阻断 |
| 审计到 WORM P95 | ≤ 60 秒 | 5 分钟 | 安全治理 | 超标 10 分钟阻断 |
| 心跳丢失发现 | ≤ 30 秒 | 连续 | 游戏服务端 | Critical 告警未关闭时阻断 |

发布流水线运行 `Scripts/Test-SloReleaseGate.ps1`。指标缺失按失败处理，不能把“无数据”解释为健康。Grafana 的 `Mahjong / SLO Governance` 面板用于查看预算、燃烧率和延迟。

## 多集群发现与故障隔离

Lobby/Allocator 使用 `SourceId + Generation + RegistrationId` 刷新短租约。相同 `SourceId` 只接受更高代次；同一 Region/Cluster/Lobby/Node 路由冲突时按 SourceId 确定性选主并隔离冲突来源。租约过期只移除对应地域来源，不影响其他地域。

处置步骤：

1. 在 `/admin/v1/topology` 确认来源状态、冲突对象与租约到期时间。
2. 核对部署系统中的代次和实例身份，不得直接在 Admin 内存目录手工改状态。
3. 停止旧实例刷新，提升新实例 Generation 后重新注册。
4. 验证目标地域恢复、其他地域无错误预算异常，并保存 TraceId。

## WORM/SIEM 与审计链异常

归档 Outbox 对外提交使用幂等键；锚定任务重新计算 `PreviousHash -> RecordHash` 链并把链头提交到独立信任域。`integrity_failed` 告警必须按安全事件处理。

1. 立即停止高风险管理命令执行，但保持只读监控和 Outbox 数据。
2. 保存数据库只读快照、WORM 回执、链头、相关 TraceId 与时钟状态。
3. 不得自动修改或“修复”原审计记录；在隔离副本中定位首个不一致序号。
4. 对比外部锚点与数据库链头，判断数据库篡改、归档缺失或外部端点回滚。
5. 安全负责人审批后恢复归档；确认同一幂等键没有生成重复记录。

## ABAC 与 Break-glass

RBAC 只授予能力，ABAC 继续校验地域、有效班次、案件分派和补偿金额。敏感历史与证据读取必须同时满足案件直接关联和身份系统案件分派；审计角色也不能仅凭角色枚举案件。

Break-glass 必须具备强 MFA、身份提供方签发的不超过 15 分钟截止时间，以及 `X-Break-Glass-Reason` 明确原因。每次使用产生 Critical 结构化日志和 `mahjong_admin_break_glass_uses` 指标；事后 24 小时内完成复核。Break-glass 不允许修改对局结果。

## 演练与恢复验收

季度演练按 `Contracts/Monitoring/governance-drills-v1.yaml` 执行。生产破坏性注入必须在已审批维护窗口内使用隔离目标。RTO 目标 300 秒，RPO 目标 60 秒。每次恢复必须验证：

- 未受影响地域持续可查；
- 管理 Outbox、补偿和奖励撤销没有重复执行；
- 审计链连续且外部锚点一致；
- OIDC 撤权在 10 分钟内生效；
- 告警触发时间、恢复时间与 TraceId 已进入工单；
- 未达标项具有责任人、截止时间和再次演练日期。
