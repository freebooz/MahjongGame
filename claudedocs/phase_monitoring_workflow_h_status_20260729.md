# 工作流 H：多集群、SLO 与治理执行报告

日期：2026-07-29  
范围：MON-070～MON-074  
结论：代码、契约、可观测性规则、生产配置模板与自动化门禁已完成；真实生产 WORM/SIEM 凭据注入、跨地域灾备切换和破坏性演练仍须在获批维护窗口执行。

## 1. 完成情况

| 任务 | 状态 | 主要证据 |
|---|---|---|
| MON-070 多 Region/Cluster/Lobby/Node 动态发现 | 已完成 | `TopologyRegistry`、Lobby/Allocator 租约刷新器、动态监控客户端、Angular 地域/集群/大厅/节点筛选 |
| MON-071 首批 SLO | 已完成 | `slo-v1.yaml`、Prometheus recording/alert rules、Grafana SLO 面板、发布门禁脚本 |
| MON-072 WORM/SIEM 与审计链锚定 | 已完成（外部端点待生产接入） | 审计 Outbox、链重算、外部链头锚定、幂等键、失败/积压/完整性告警、篡改测试、取证手册 |
| MON-073 RBAC + ABAC | 已完成 | 地域、班次、案件归属、金额阈值、强 MFA Break-glass、结构化决策日志与告警 |
| MON-074 故障、容灾和安全演练 | 自动化契约完成；生产演练待窗口 | 演练目录、RTO/RPO、不重复执行不变量、CI 治理门禁与运行手册 |

## 2. MON-070 实现

- Lobby/Allocator 以 `RegistrationId + SourceId + Generation` 向 Admin 周期刷新短租约。
- 相同 SourceId 只接受更高 Generation；同代次不同进程标识拒绝覆盖。
- 相同 Region/Cluster/Lobby/Node 路由冲突时按 SourceId 字典序确定有效来源，其余标记 Conflict。
- 租约过期只隔离对应来源；多来源聚合继续返回其他地域数据及来源健康状态。
- Room/Instance 查询支持 Region、Cluster、Lobby、Node 服务端筛选和稳定游标。
- 移除玩家 Presence 的固定 `primary`，改为真实 LobbyId。
- 遗留实例 ID 跨集群冲突采用 SourceId/ClusterId 稳定去重，避免聚合崩溃。

## 3. MON-071 实现

- Admin API 30 天可用性目标 99.9%，计算错误预算和 1h/6h 燃烧率。
- 遥测新鲜度、高危审批到执行、审计到 WORM 均由 OpenTelemetry Histogram 采集。
- Dedicated Server 心跳丢失阈值为 30 秒。
- Grafana `slo-governance.json` 展示预算、燃烧率和 P95 延迟。
- `Test-SloReleaseGate.ps1` 在指标缺失、预算耗尽或快速燃烧时 fail-closed。
- Prometheus 保留期提高到 35 天，覆盖 30 天 SLO 窗口。

## 4. MON-072 实现

- 审计归档使用独立数据库身份和 Outbox 重试，不静默丢弃。
- 周期校验 `PreviousHash -> RecordHash`，发现正文、前序哈希或记录哈希异常时发出 Critical 指标并停止宿主，阻止证据不可信时继续执行管理命令。
- 链头通过 HTTPS 提交独立 WORM/SIEM，`Idempotency-Key` 使用 head hash；临时网络失败保留状态并在下一周期重试。
- 已增加数据库篡改测试，验证异常在网络提交前被拒绝。
- 运行手册明确禁止自动修补原审计记录，并规定快照、外部锚点和 TraceId 取证步骤。

## 5. MON-073 实现

- 企业令牌读取地域、班次、案件分派和 Break-glass 截止时间属性。
- 启用 ABAC 后，Room/Instance/Topology 查询必须显式指定获授权地域。
- 敏感玩家历史和证据包要求人员与案件直接关联，同时命中身份系统案件分派；相同角色无案件归属会拒绝。
- 达到 100000 最小资产单位的补偿要求 `governance.senior-approver`。
- Break-glass 要求 MFA、最长 15 分钟、明确原因；每次使用记录 Critical 日志、TraceId 和告警指标。
- 生产启动验证强制企业 OIDC、MFA、HTTPS 和 ABAC，禁用本地共享管理员身份。

## 6. MON-074 实现

`governance-drills-v1.yaml` 定义以下演练：

- 地域注册租约过期和冲突；
- 单一监控来源超时/503；
- Redis 分区和 PostgreSQL 主备切换；
- WORM 端点不可用和审计链篡改；
- OIDC 撤权与 Break-glass 到期。

统一目标为 RTO 300 秒、RPO 60 秒，并强制验证管理命令、补偿、资产和审计均不重复。生产破坏性注入未在本次代码执行中启动，避免在没有维护窗口、审批工单和隔离目标时影响现有对局。

## 7. 验证结果

- .NET Release 构建：0 警告、0 错误。
- 非外部持久化测试：148 项通过；新增治理专项 5 项通过。
- Angular 22 TypeScript 类型检查和生产构建通过。
- 工作流 H、可观测性、容量、调查闭环契约脚本通过。
- Prometheus `promtool`：24 条规则通过，其中 SLO 规则 15 条。
- Grafana SLO dashboard JSON 解析通过。
- Linux 服务与可观测性 Docker Compose 配置校验通过。

## 8. 上线前必须完成

1. 在密钥系统注入互不复用的注册、Lobby 只读、Allocator 只读、WORM 和管理命令凭据。
2. 在企业身份提供方配置 `mahjong_regions`、`mahjong_shift`、`mahjong_cases`、`mahjong_break_glass_until` Claim 与高级审批角色。
3. 使用真实 WORM/SIEM 测试租户验证链头回执、重复幂等键和跨重启连续性。
4. 在审批维护窗口按演练目录执行跨地域、Redis/PostgreSQL 和平台故障注入，并将实测 RTO/RPO 回填工单。
5. 发布流水线使用在线 Prometheus 模式运行 SLO 门禁；静态 `ContractOnly` 只能验证规则存在，不能替代当前错误预算判定。
