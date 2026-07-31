# 阶段9：NATS JetStream、Outbox、Inbox 与 BackgroundWorkers

## 1. 阶段范围与结论

本阶段在不删除既有同步调用、不改变对外 API、不改变结算本地事务的前提下，引入可靠的跨服务事件传播链路：

```text
业务事务（PostgreSQL）
  └─ 同事务写入服务自有 platform_outbox
       └─ OutboxPublisherWorker（SKIP LOCKED + 租约）
            └─ JetStream 发布确认（Nats-Msg-Id = event_id）
                 └─ Durable Pull Consumer（显式 ACK）
                      └─ Inbox + 投影同事务提交
                           ├─ 战绩投影
                           ├─ 排行榜投影
                           └─ 审计投影
```

登录立即响应、Room 状态 CAS、Join Ticket、Dedicated Server 动作校验、结算数据库事务继续使用原同步或本地事务路径。NATS 中断只延迟传播，不回滚已经成功的业务事务。

## 2. 实施前真实状态

- 阶段2已存在版本化 `EventEnvelope`、通用 PostgreSQL Outbox/Inbox 和 API 幂等基础构件。
- 事件发布器只有内存测试实现，没有 NATS 生产传输。
- Auth 使用旧 `integration.identity_outbox`，仅覆盖部分会话撤销；Lobby 与 GameData 未形成统一可发布信封。
- 不存在统一 Workers 部署单元、Durable Consumer、人工失败表、Consumer Lag 指标和 NATS 部署清单。
- 既有服务间 HTTP 调用仍是主路径；本阶段未提前移除。

## 3. Subject 与契约

事件信封沿用阶段2字段：`event_id`、`event_type`、`schema_version`、聚合标识/版本、发生时间、生产者、Trace/Correlation/Causation、幂等键和 Payload。传输 Subject 与契约事件类型由唯一白名单映射，未知事件或非 v1 Schema 失败关闭。

| 契约 | JetStream Subject |
|---|---|
| SessionCreated | `identity.session.created.v1` |
| SessionRevoked | `identity.session.revoked.v1` |
| RoomCreated | `room.created.v1` |
| RoomStateChanged | `room.state.changed.v1` |
| AllocationRequested | `allocation.requested.v1` |
| GameServerAllocated | `gameserver.allocated.v1` |
| GameServerReady | `gameserver.ready.v1` |
| PlayerConnected / PlayerDisconnected | `player.connected.v1` / `player.disconnected.v1` |
| MatchStarted / MatchFinished | `match.started.v1` / `match.finished.v1` |
| SettlementCommitted | `settlement.committed.v1` |
| RoomTerminated | `room.terminated.v1` |

Stream 为 `MAHJONG_PLATFORM_EVENTS`。开发环境单副本、生产模板三副本；保留14天、单条最大1 MiB、总量默认10 GiB，`event_id` 同时作为 `Nats-Msg-Id`。重复发布返回 Duplicate ACK 时视为已持久化成功，最终业务幂等仍由 Inbox 保证。

## 4. 生产者与数据所有权

| 所有者 | Outbox Schema | 产生事件 | 事务边界 |
|---|---|---|---|
| IdentityApp | `identity_integration` | SessionCreated、SessionRevoked | 会话创建、轮换、撤销同事务 |
| LobbyControlApp | `lobby_integration` | 房间、分配、GameServer、连接、Match、终止事件 | 权威房间行/事件历史触发器同事务 |
| GameDataApp | `game_data_integration` | SettlementCommitted | 不可变结算、战绩、证据清单与 Outbox 同事务 |

Lobby 的 GameServer 事件表示“Lobby 已持久化并接受该绑定/就绪事实”，不是让 Lobby 直接调用 Kubernetes。完整私有手牌、Access Token、Refresh Token 和 Join Ticket 不进入事件。

迁移：Auth `0004_jetstream_outbox`、Lobby `0005_jetstream_outbox`、GameData `0002_jetstream_outbox_envelope`、Workers `0001_workers`。GameData 升级会把旧的 payload-only Outbox 行包装为完整信封；回滚只停止新生产入口并保留未排空 Outbox，不删除已提交业务事件。

## 5. Workers

项目位于 `Services/Apps/GuiyangMahjong.Workers`，包括：

- 多来源 Outbox 发布：批量领取、`FOR UPDATE SKIP LOCKED`、租约恢复、发布确认、指数退避、最大重试、错误摘要、失败状态和归档；
- 三个 Durable Consumer：`game-record-projection-v1`、`leaderboard-projection-v1`、`audit-projection-v1`；
- Inbox：`consumer_name + event_id` 唯一，业务投影与 Inbox 完成同事务；ACK 丢失后重投快速确认；
- 乱序保护：按 `aggregate_version` 检查投影检查点，旧事件确认但不覆盖新状态；
- 毒消息：未知 Schema、Subject 伪装、损坏信封或超过最大投递进入 `worker_integration.failed_events`，记录人工处理状态并 TERM；
- 维护：Inbox 清理、已发布 Outbox 归档、消息积压监控；Session/Room 清理采用数据所有者 HTTP 维护命令调度器，默认关闭，避免 Worker 跨 Schema 删除业务数据；
- 健康检查：`/health/live`、`/health/startup`、`/health/ready`；
- 可观测性：Producer/Consumer Span、Trace/Correlation 传播、发布/失败/重复/DLQ/耗时/Lag 指标和结构化日志。

Workers 可以水平扩容。同一 Durable Consumer 由多个实例共享；数据库租约与 Inbox 唯一约束保证实例崩溃、ACK 丢失和重复投递不会重复产生投影副作用。

## 6. 权限与配置

新增 `mahjong_workers` LOGIN 与 `mahjong_workers_rw` NOLOGIN 权限角色。生产者只向自有 Outbox `INSERT`；Worker 仅能读取、领取、标记和归档三类 Outbox，并写入 `worker_integration`、`worker_projection`，不能访问 Auth、Room、资产或结算业务表，也没有 Kubernetes ServiceAccount Token。

关键配置均支持 `Workers__*` 环境变量覆盖：

- `PostgresConnectionString`：Worker 自有连接；
- `NatsUrl`、`NatsUsername`、`NatsPassword`：密码独立于 URL，禁止日志输出；
- `StreamReplicas`：Compose 为1，Kubernetes 为3；
- `OutboxSources`：受控来源名称、Schema 和最小权限连接；
- 批量、租约、重试、保留期和 Lag 阈值；
- `SessionCleanup`、`RoomCleanup`：数据所有者维护端点、专用令牌和周期，默认关闭。

生产运行时禁止 DDL。迁移账号独立执行 Schema 和授权，真实凭据只能由 Secret/External Secrets 注入。

## 7. 部署与监控

- Compose：`nats:2.12.12-alpine`、持久卷、健康检查、`prometheus-nats-exporter:0.17.3`、Workers 和独立资源限制；NATS 端口只绑定宿主机 loopback。
- Kubernetes：3节点 StatefulSet、每 Pod PVC、PDB、资源限制、健康探针、NetworkPolicy、NATS exporter、2副本 Workers；不创建公网 Ingress。
- Prometheus：采集 NATS exporter 与 OTel Collector；告警覆盖 NATS 不可用、Consumer Lag 和 DLQ。
- Stream/Consumer 声明模板位于 `Deploy/nats`；应用启动也会幂等校准 Stream 和 Durable Consumer。

## 8. 兼容与回滚

兼容策略：旧 HTTP 调用和外部 API 保持不变；事件只用于状态传播、读模型、排行榜和审计。旧 Auth Outbox 表暂时保留但新代码不再写入，待观测窗口结束后另行治理。

回滚顺序：

1. 停止部署新生产者版本，禁止生成新的标准 Outbox 行；
2. 保持 NATS 与旧 Workers 运行，排空 `Pending/Processing`；若 NATS 故障则保留数据库行，不手工标记成功；
3. 停止 Workers 和 Consumer；同步主链路仍可工作；
4. 执行对应 down migration。Lobby 回滚故意保留 Outbox/归档表；
5. 只有在排空、审计保留和审批完成后，后续迁移才可删除遗留消息表。

## 9. 验证基线

- .NET Release 全解决方案构建：通过，0警告、0错误；
- Workers 外部集成测试：7/7，通过真实 PostgreSQL 17 与 NATS 2.12.12，并验证重复结算事件只更新一次读模型且不存在资产/奖励写表；
- BuildingBlocks PostgreSQL 事务/并发测试：18/18；
- Auth 外部 PostgreSQL 测试：20/20；Lobby PostgreSQL+Redis 测试：70/70；
- NATS 本地/生产配置 `nats-server -t`：通过；exporter 实际启动并读取 JetStream 监控端点；
- Compose 两套配置校验：通过；Prometheus 配置及27条规则校验：通过；
- Kubernetes 全部 YAML 解析：通过；当前机器未配置可访问的 Kubernetes API，因此没有执行服务端 dry-run。

## 10. 已知边界

- Session/Room 过期清理调度器已经存在，但默认关闭；启用前必须由 Identity/Lobby 数据所有者提供经认证、幂等的维护端点。本阶段不允许 Worker 直接越权删除业务表。
- 未引入 Kafka/RabbitMQ，也未把结算本地事务或 Room CAS 改为最终一致。
- 生产 NATS 模板使用命名空间凭据和 NetworkPolicy；正式生产上线前应按平台证书体系启用 NATS TLS 或由已批准的服务网格提供双向加密。
