# 实时监控告警处置手册

所有处置先记录告警时间、环境、TraceId、受影响服务和关联工单。任何终止实例、禁止加入或解散房间操作必须进入 Admin 二次确认/审批流程，禁止从 Grafana 直接执行写操作。

## MahjongHeartbeatMissing

目标：从最后一次有效心跳起 30 秒内触发。先确认 Collector、Lobby 和 Prometheus 是否同时正常，再用 Loki 查询最近心跳失败日志。按 TraceId 查看 Lobby→Allocator Trace，核对实例是否被调度、端口是否可达。仅在确认单实例异常后，由有权人员通过 Admin 审批终止实例；全局采集故障不得批量终止游戏服。

## MahjongDedicatedServerCpuHigh

确认持续时间和构建版本，联查 Tick、RPC 与网络速率。若仅单版本升高，暂停该版本继续扩容并创建发布回退工单；若 RPC 同时上升，按 RPC Storm 处理。不要仅凭 CPU 告警修改对局结果。

## MahjongDedicatedServerMemoryHigh

检查内存趋势是否单调增长、房间生命周期是否无法结束以及同构建版本的基线。先禁止异常实例接受新房间，再导出房间日志和回放；终止实例须审批并确认结算/Outbox 状态。

## MahjongDedicatedServerTickSlow

在 Tempo 中定位慢心跳前后的 RPC/结算 Span，并联查 CPU、内存和 RPC。正在结算时优先保护幂等 Outbox；若必须迁移或终止，先确认结算状态与恢复策略。

## MahjongDisconnectRateHigh

按构建版本、区域和时间窗比对，排除 Collector 重启造成的假象。查询连接状态事件流并核对大厅、Allocator 和 Dedicated Server 的 Trace。大面积掉线升级为网络事故；单房间异常进入争议调查和回放保全。

## MahjongRpcStorm

确认是否来自合法压测/发布。检查固定白名单 RPC 分布、拒绝率和客户端版本；必要时启用入口限流或维护模式。禁止把 PlayerId 加入 Prometheus 标签定位，改用异常 Trace 的 RoomId/PlayerId 查询受控日志。

## MahjongServiceErrorRateHigh

按 `service` 标签定位服务，检查最近发布、依赖熔断和下游 5xx。通过 TraceId 逐跳检查 Admin、Auth、Lobby、Allocator、PlayerData。若使用缓存降级，界面必须保持陈旧标识，高危写操作继续失败关闭。

## MahjongAdminCommandBacklogSuspected

检查 Admin dispatcher 日志、数据库 Outbox 租约、执行凭据和目标服务可用性。不得手工把命令直接标记为成功；修复依赖后依靠幂等重试恢复。终止重试须保留操作前后状态、审批、TraceId 和工单。

## MahjongAuditArchiveBacklogSuspected

检查归档端点、专用凭据、TLS 和 Outbox 租约。归档恢复前暂停非必要高危操作；不得删除待归档审计记录。恢复后核对成功计数、重复冲突是否被当作幂等成功，以及不可变存储中的最终记录。

## 降噪、静默与升级

## MahjongNatsJetStreamUnavailable

先检查三个 NATS Pod、JetStream `/healthz`、PVC 容量和网络策略，再检查 exporter 本身。业务写事务不得因该告警回滚；确认各服务的 `platform_outbox` 持续保留未发布记录。恢复后观察发布确认、Outbox 积压和 Durable Consumer Pending 是否收敛，禁止手工把未确认消息标成已发布。

## MahjongJetStreamConsumerLagHigh

按 `consumer` 定位战绩、排行榜或审计投影，核对 Worker 副本、Inbox 数据库延迟、毒消息和下游锁等待。可水平扩容 Worker，但不得删除 Durable Consumer 或跳过 Inbox；乱序事件应由 `aggregate_version` 检查转为无副作用确认。

## MahjongWorkerDeadLetterDetected

查询 `worker_integration.failed_events` 的事件 ID、Subject、错误摘要和 TraceId，确认是未知 Schema、内容损坏还是超过最大投递次数。修复消费者后通过受控工单重新投递原事件；不得编辑结算载荷、生成新事件 ID 规避幂等或直接修改投影结果。

- 相同 `alertname + severity + service` 合并；Critical 抑制同名 Warning。
- 仅对已登记维护窗口创建有时限静默，静默必须关联工单和负责人。
- Critical 首次等待 15 秒、30 分钟重复；Warning 默认 4 小时重复。持续两个重复周期或影响多个服务时升级值班负责人和安全/支付/客服相关负责人。
- 关闭告警前必须同时具备：指标恢复、相关错误日志停止、关键 Trace 成功、工单记录恢复证据。
