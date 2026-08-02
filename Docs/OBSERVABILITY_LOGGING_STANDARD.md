# 结构化日志、指标与追踪标准

## 日志契约

所有 .NET 服务通过 `GuiyangMahjong.Observability` 输出单行 JSON；Dedicated Server 的控制面桥接输出同一字段集合。必填顶层字段为：

`Timestamp`、`Level`、`Service`、`Environment`、`TraceId`、`RoomId`、`PlayerId`、`MatchId`、`ServerInstanceId`、`EventId`、`Category`、`Message`、`Properties`。

无上下文的可选业务标识以 `null` 或空字符串表示，禁止删除字段。业务事件必须优先携带 EventId；HTTP 请求以 `X-Trace-Id` 传播业务 TraceId，同时由 OpenTelemetry 自动传播 W3C `traceparent`。

## 敏感数据规则

拒绝记录密码、完整访问令牌、Authorization/Cookie、连接字符串、签名密钥、卡号/CVV、聊天正文、支付正文和完整 IP。IP 仅允许掩码形式，例如 `10.20.30.*`。异常只输出受控类型和摘要，不输出可能包含请求正文的原始异常对象。

应用格式器、Activity Processor 和 Collector 分别执行脱敏。新增字段前必须扩展 `SensitiveDataSanitizer` 和契约测试；不得把“仅在内网”作为绕过理由。

## 指标基数

Prometheus 标签只允许服务名、HTTP 方法、路由模板、状态码类别、受控生命周期、构建版本、管理动作枚举和结果枚举。RoomId、PlayerId、MatchId、请求 ID、工单号不得成为标签。

唯一例外是 `mahjong_room_heartbeat_last_seen_seconds`：它允许
`server_instance_id`，但生产者使用 24 小时淘汰和 10000 实例硬上限。该例外用于在没有新事件时计算单实例心跳年龄，不得复制到其他累计指标。

业务 ID 放在日志/Span 中。指标异常通过 exemplar 或同一时间窗进入 Tempo，再以 TraceId 跳转 Loki，实现“集群 → 服务 → Trace → 实例/房间”的安全下钻。

## 采样与留存

- 父级采样优先，默认开发采样率 10%；错误和高危管理链路在生产 Collector 策略中应单独保留。
- Loki 开发保留 7 天，Prometheus 15 天，Tempo 7 天。
- 生产审计记录不以 Loki 作为唯一存档，仍由不可变审计归档 Outbox 交付。

## CI 门禁

`Scripts/Test-ObservabilityContracts.ps1` 检查字段、UE 接入、Collector 脱敏、告警和仪表盘；`StructuredLoggingContractTests` 验证 JSON 格式、字段拒绝清单、令牌/卡号/IP 脱敏；`CentralLogQueryClientTests` 验证服务端只读代理和 RoomId 查询范围。
