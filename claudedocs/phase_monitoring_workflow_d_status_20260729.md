# 工作流 D：结构化日志、指标、追踪与告警验收记录

日期：2026-07-29  
范围：MON-030、MON-031、MON-032、MON-033、MON-034  
状态：已完成（本地可观测性栈正在运行）

## 1. 实施结论

工作流 D 已形成“应用生产 → Collector 双层脱敏 → Loki/Tempo/Prometheus → Grafana/Alertmanager → Admin 受控导出”的闭环：

- 五个 .NET 服务共用 `GuiyangMahjong.Observability`；
- Dedicated Server 控制面桥接输出同字段单行 JSON；
- `X-Trace-Id` 业务链路与 W3C `traceparent` 技术链路同时传播；
- OpenTelemetry 1.17.0 输出日志、Trace、RED/USE 与麻将业务指标；
- Collector 对凭据、支付、聊天和原始 IP 属性执行第二层清理；
- Loki、Tempo、Prometheus、Alertmanager、Grafana 和只读查询网关已由 Docker Compose 启动；
- Admin 只在房间日志导出案件审批通过后查询 Loki，查询凭据不下发浏览器；
- 四个 Grafana 看板和九条 Prometheus 告警规则已供应；
- 中文 Runbook 覆盖告警确认、日志/指标/Trace 联查、静默、抑制和升级。

## 2. 关键实现

### 2.1 统一日志与脱敏

固定字段：

`Timestamp`、`Level`、`Service`、`Environment`、`TraceId`、`RoomId`、`PlayerId`、`MatchId`、`ServerInstanceId`、`EventId`、`Category`、`Message`、`Properties`。

应用层拒绝密码、令牌、Authorization/Cookie、连接字符串、签名密钥、卡号/CVV、聊天/支付正文和完整 IP。Collector 再删除敏感属性并清理 Bearer 与卡号模式。异常只记录类型和受控摘要。

### 2.2 Trace 与上下文

- Admin → Auth/Lobby/Allocator/PlayerData 使用 HttpClient instrumentation 自动传播 `traceparent`；
- 同时使用 `X-Trace-Id` 传播业务 TraceId；
- Lobby 房间创建、GameServer 注册和心跳处理增加内部 Span；
- Span 属性导出前经过敏感字段 Processor；
- Grafana 配置 Tempo→Loki 时间窗联查与 Prometheus exemplar 目标。

### 2.3 指标

- HTTP RED：请求数、失败数、耗时；
- .NET Runtime USE：运行时资源指标；
- 房间/玩家：活跃房间、连接玩家、心跳接收、单实例最后心跳；
- Dedicated Server：Tick、FPS、CPU、内存、进出网络、RPC、掉线；
- Admin：命令领取批次、命令结果、审计归档领取批次、归档结果；
- 监控聚合：工作流 C 已有来源耗时、失败、超时和熔断指标。

RoomId、PlayerId、MatchId 不进入指标标签。ServerInstanceId 仅用于
`mahjong_room_heartbeat_last_seen_seconds`，有 24 小时淘汰和 10000 实例硬上限。

### 2.4 集中日志与 Admin 安全代理

Admin 新增 `ICentralLogQueryClient`，通过只读 Nginx 网关调用 Loki：

- 查询强制固定 RoomId；
- BaseUrl 与 QueryToken 只在服务端配置；
- 单次最多 5000 条，默认导出 1000 条；
- 只有既有 `RoomLogExport` 案件、角色、工单和审批校验通过后才能导出；
- 导出物包含操作员、时间、TicketId、TraceId、CaseId、水印、审批快照与集中日志；
- 查询失败返回 `ADMIN_CENTRAL_LOG_UNAVAILABLE` 503，不生成伪完整证据；
- 日志查询/导出继续写入不可变审计链。

### 2.5 平台与访问边界

本地端口：

| 组件 | 地址 | 访问边界 |
|---|---|---|
| Grafana | `http://127.0.0.1:13000` | 强认证，禁止匿名/注册 |
| Loki 查询网关 | `http://127.0.0.1:13100` | Bearer，只允许 query/query_range |
| OTLP gRPC/HTTP | `127.0.0.1:4317/4318` | 本机采集入口 |
| Prometheus | `http://127.0.0.1:19090` | 本机 |
| Alertmanager | `http://127.0.0.1:19093` | 本机 |
| Loki/Tempo 原生端口 | 未暴露宿主机 | 仅 Compose 内网 |

Loki/Tempo 保留 7 天，Prometheus 保留 15 天；Loki 配置查询窗口、行数、摄入速率、突发和基数上限。

## 3. 仪表盘与告警

供应看板：

1. 管理服务与聚合可靠性；
2. 房间与集群下钻；
3. Dedicated Server 运行质量；
4. 审批、命令与审计归档。

告警：

1. 单实例心跳超过 20 秒或采集管线 30 秒完全静默；
2. Dedicated Server CPU 高；
3. Dedicated Server 内存高；
4. Tick 超过 50ms；
5. 掉线率高；
6. RPC 风暴；
7. 服务 5xx 比例高；
8. Admin 命令积压疑似；
9. 审计归档积压疑似。

Alertmanager 按 `alertname + severity + service` 分组，Critical 抑制同名 Warning，并配置分级重复间隔。所有写操作仍只能从 Admin 审批流程发起，Grafana 无管理写入口。

## 4. 验收证据

### 4.1 构建与测试

- .NET solution build：0 警告、0 错误；
- UnrealBuildTool 单文件门禁：`GuiyangGameServerBridge.cpp` 编译成功，输出
  `Binaries/Win64/GuiyangMahjongServer.exe`；
- 非外部持久化测试：136 通过、0 失败；
- Admin 集中日志与结构化日志契约：包含在 55 个 Admin 测试中并通过；
- `Test-ObservabilityContracts.ps1`：通过；
- Docker Compose 配置：通过；
- Grafana 四个 Dashboard JSON：通过 CI 门禁并完成供应；
- Prometheus：2 个规则组、9 条规则成功加载。

### 4.2 运行验收

- 七个可观测性容器均为 Running；
- Collector 日志显示日志、指标、Trace 三条管线 Ready；
- Auth 冒烟请求的结构化日志可在 Loki 以 `service_name` 和 TraceId 查询；
- 同一请求可在 Tempo 查询到 `GuiyangMahjong.Auth` 根 Trace；
- Prometheus 收到 `mahjong_http_server_requests_total` 和耗时直方图；
- 无心跳场景 `MahjongHeartbeatMissing` 已进入 Firing，并到达 Alertmanager；
- Loki 查询网关匿名访问返回 401，非允许路径返回 404，正确 Bearer 查询成功；
- Grafana `/api/health` 返回数据库 `ok`，Dashboard provisioning 完成。

## 5. 验收矩阵

| 工作项 | 结果 |
|---|---|
| MON-030 结构化日志 | 通过：统一字段、.NET/UE 接入、应用与 Collector 双层脱敏、CI 契约 |
| MON-031 集中日志 | 通过：Loki 留存/限额、只读网关、Admin 审批查询导出、审计 |
| MON-032 OpenTelemetry | 通过：跨服务传播、Lobby 关键 Span、Tempo、采样与属性过滤 |
| MON-033 Prometheus/Grafana | 通过：RED/USE/业务指标、四看板、实例心跳 Gauge、基数约束 |
| MON-034 告警/Runbook | 通过：九条规则、Alertmanager 分组/抑制、中文处置手册、运行触发验证 |

## 6. 生产深化边界

- 当前是单机开发/预生产基线；生产应把 Loki/Tempo 改为多副本与对象存储，并为 Grafana 接入企业 OIDC。
- 本次运行栈使用进程内随机初始密钥完成验证，未把密钥写入仓库。重建前应复制 `.env.example` 为被 Git 忽略的 `.env`，由密钥管理系统提供真实值。
- 现有游戏服务容器尚未为本次验证整体重建；部署配置已经支持
  `OBSERVABILITY_ENABLED=true` 和 OTLP Endpoint。正式滚动发布应结合维护窗口执行。
- 本轮没有重新 Cook/Stage 完整 Dedicated Server 包；已对本次修改的 C++ 翻译单元执行
  UnrealBuildTool 编译。完整发布包应在下一次服务器滚动发布门禁中生成。
- 本地告警 receiver 只显示在 Alertmanager UI；生产必须替换为企业告警网关/Webhook 并进行值班升级演练。
- Loki/Prometheus 是运维观测存储，不替代不可变审计存档和对局结果权威存储。

## 7. 关键文档

- `Docs/OBSERVABILITY_LOGGING_STANDARD.md`
- `Docs/RUNBOOKS/OBSERVABILITY_ALERTS.md`
- `Deploy/observability/README.md`
- `Scripts/Test-ObservabilityContracts.ps1`
