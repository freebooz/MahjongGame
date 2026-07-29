# 工作流 C：Admin 聚合可靠性验收记录

日期：2026-07-29  
范围：MON-020、MON-021、MON-022、MON-023  
状态：已完成

## 1. 实施结论

Admin 的房间、玩家和 Dedicated Server 监控已从“任一下游失败导致整次聚合失败”调整为来源隔离模型：

- Auth、Lobby、Allocator、PlayerData 使用各自的硬超时配置；
- 调用方取消向下游传播，下游忽略取消时由 `Task.WaitAsync` 保证 Admin 请求线程在上限内返回；
- 来源状态受控为 `Healthy/Degraded/Unavailable/Stale`；
- Lobby、Allocator 和允许缓存的 Auth 列表保存有界、带 TTL 和单调版本的最后成功快照；
- 连续失败打开来源级熔断器，恢复窗口只允许一个半开探测，并按指数退避；
- 所有错误返回受控代码和中文摘要，不回传内部地址、凭据或原始异常正文；
- 高危操作使用专用实时读取方法，任何缓存、熔断或不可用来源均返回
  `ADMIN_FRESH_STATE_REQUIRED`，不会把陈旧页面状态作为执行依据；
- 管理台已迁移为 Angular 22.0.8 + TypeScript 6.0.3，并按面板独立降级。

## 2. 关键实现

### 2.1 后端可靠性边界

- `MonitoringSourceReliabilityService`
  - 独立硬超时与取消传播；
  - `admin_monitoring_source_timeouts`、`admin_monitoring_source_failures`、
    `admin_monitoring_circuit_rejections` 计数器；
  - `admin_monitoring_source_duration_ms` 耗时直方图；
  - 来源级 Closed/Open/HalfOpen 熔断状态；
  - 最后成功快照、TTL、版本和最大条目限制；
  - 超时、请求失败、熔断打开等安全错误代码。
- `MonitoringAggregationService`
  - Lobby 与 Allocator 并行独立收敛；
  - Allocator 故障时保留 Lobby 房间，集群/节点字段为空或来自未过期快照；
  - 房间 runtime 和 timeline 独立降级；
  - 总览和房间详情返回可靠性元数据。
- `PlayerMonitoringService`
  - Auth 主档与 Lobby 房间/在线状态独立收敛；
  - Lobby 故障不隐藏 Auth 玩家基础资料；
  - 玩家详情附带来源可靠性元数据；
  - 未脱敏详情不进入最后成功快照。
- `HttpPlayerDataMonitoringClient`
  - 使用 PlayerData 专用 MonitoringToken 调用
    `/internal/monitoring/health`；
  - 不复用 Wallet 管理命令凭据，不读取资产、支付或聊天证据。

### 2.2 响应契约

`MonitoringSourceHealth` 提供：

- Source、Status、Enabled；
- ObservedAtUtc、LastSuccessAtUtc、DataAgeSeconds；
- StaleAfterSeconds；
- ErrorCode、安全中文 Message；
- CircuitState、SnapshotVersion；
- TimeoutCount、FailureCount。

`MonitoringReliabilityMetadata` 提供：

- GeneratedAtUtc；
- Partial；
- SafeForHighRiskActions；
- Sources。

新增只读接口：

```text
GET /admin/v1/source-health
```

该接口要求 `room.viewer`，不会向匿名用户暴露来源降级状态。

### 2.3 Angular 22 管理台

源码目录：

```text
Services/GuiyangMahjong.Admin/ClientApp
├─ angular.json
├─ package.json
├─ package-lock.json
├─ tsconfig.json
└─ src
   ├─ index.html
   ├─ main.ts
   ├─ styles.css
   └─ app
      ├─ app.component.html
      ├─ app.component.ts
      └─ admin-console.ts
```

页面实现：

- 四来源健康卡；
- 最后成功时间、数据年龄、陈旧阈值、快照版本和熔断状态；
- 总览、玩家、房间、实例、审批账本等请求按面板独立收敛；
- 任一面板失败时显示中文错误，其他面板继续刷新；
- 房间和玩家详情在管理入口附近显示数据是否适合高危操作；
- `dotnet publish` 自动执行 `npm ci`、TypeScript 类型检查和 Angular 生产构建。

根目录 `AGENTS.md` 已增加全局策略：管理前端统一使用 Angular 22 + TypeScript，
源码位于 `ClientApp`，不得手工编辑生成的 `wwwroot` 产物。

## 3. 配置

```text
Admin__MonitoringReliability__CircuitFailureThreshold=3
Admin__MonitoringReliability__CircuitBreakSeconds=10
Admin__MonitoringReliability__CircuitMaxBreakSeconds=120
Admin__MonitoringReliability__StaleAfterSeconds=15
Admin__MonitoringReliability__SnapshotTtlSeconds=300
Admin__MonitoringReliability__MaxSnapshotEntries=128

Admin__Auth__TimeoutSeconds=5
Admin__Lobby__TimeoutSeconds=5
Admin__Allocators__0__TimeoutSeconds=5

Admin__PlayerData__Enabled=true
Admin__PlayerData__BaseUrl=http://mahjong-player-data:8080
Admin__PlayerData__MonitoringToken=<专用只读监控凭据>
Admin__PlayerData__TimeoutSeconds=5
```

生产环境启用 PlayerData 监控时必须同时配置 PlayerData 服务自身的
`PlayerData__MonitoringToken`，且不得与 Wallet/Admin 命令凭据相同。

## 4. 验收证据

### 4.1 自动化测试

- Admin：47 通过，3 个需要外部 PostgreSQL 的测试按环境条件跳过，0 失败；
- PlayerData：5 通过，2 个需要外部 PostgreSQL 的测试按环境条件跳过，0 失败；
- 新增覆盖：
  - 下游忽略取消时仍在硬超时内返回；
  - 失败后使用带版本快照并标记 Stale；
  - 连续失败打开熔断且后续调用被短路；
  - Allocator 不可用时总览仍返回 Lobby 房间；
  - 高危房间读取拒绝陈旧依赖；
  - PlayerData 内部健康端点要求专用凭据。

### 4.2 前端与发布验证

- `npm run typecheck`：通过；
- `npm run build`：通过；
- Angular 初始包：139.41 kB，估算传输 39.64 kB；
- `npm audit --omit=dev`：0 个生产依赖漏洞；
- `dotnet publish -c Release`：通过，发布目录包含 Angular 哈希资源及
  Brotli/Gzip 预压缩文件；
- 浏览器运行验证：
  - Angular 根组件成功挂载；
  - 凭证对话框和刷新操作正常；
  - 健康、陈旧、不可用、未启用四种来源显示正常；
  - 模拟玩家目录返回 HTTP 503 后，页头显示“降级更新 · 1 个面板不可用”，
    玩家面板显示来源错误，而总览、房间和服务器面板继续可用。

## 5. 验收矩阵

| 工作项 | 验收结果 |
|---|---|
| MON-020 独立超时 | 通过：四来源独立配置、取消传播、受控超时错误和指标已实现 |
| MON-020 总览时间上限 | 通过：总览并行等待各来源硬超时，不被忽略取消的下游无限阻塞 |
| MON-021 部分成功 | 通过：Allocator 故障不遮蔽 Lobby 房间；Auth 故障不影响房间监控 |
| MON-021 来源可诊断 | 通过：状态、错误、最后成功时间、数据年龄和安全摘要已返回 |
| MON-022 快照与 TTL | 通过：有界缓存、TTL、版本和超期移除已实现 |
| MON-022 熔断恢复 | 通过：Closed/Open/HalfOpen、单探测、指数退避和恢复已实现 |
| MON-023 页面降级 | 通过：Angular 页面按面板独立降级，人工故障验证无白屏 |
| MON-023 高危操作 | 通过：详情显示风险，服务端强制实时读取并拒绝陈旧状态 |

## 6. 已知边界

- `npm audit` 对 Angular CLI 的开发期 MCP 传递依赖报告 3 个 moderate 问题；
  Angular 22.0.8 当前没有保持 22.x 的自动修复路径。该依赖不进入生产包，
  `npm audit --omit=dev` 为 0。后续升级 Angular 22 补丁版时应重新审查。
- 外部 PostgreSQL 并发/持久化测试需要配置测试数据库后单独执行；本次没有把
  环境缺失导致的跳过误报为通过。
- 监控指标已通过 .NET `Meter` 发布；集中采集、仪表板和告警属于后续工作流 D。
