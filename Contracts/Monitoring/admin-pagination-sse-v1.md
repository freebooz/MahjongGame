# Admin 监控分页与实时推送契约 v1

版本日期：2026-07-29

## 1. 列表分页

管理端提供以下只读列表：

- `GET /admin/v1/rooms`
- `GET /admin/v1/players`
- `GET /admin/v1/instances`

统一响应：

```json
{
  "items": [],
  "nextCursor": "opaque-or-null",
  "hasMore": false,
  "pageSize": 100
}
```

约束：

- 默认页大小为 100，服务端硬上限为 200；客户端传入更大值时按 200 执行。
- 游标是不透明值，调用方不得解析、拼接或跨筛选条件复用。
- 房间和玩家游标绑定筛选摘要；损坏、版本不兼容或筛选不匹配返回 HTTP 400。
- 房间按不可变 `CreatedAtUtc DESC, RoomId DESC` 执行数据库键集分页。
- 玩家按不可变 `CreatedAtUtc DESC, PlayerId DESC` 执行数据库键集分页。
- 新记录插入到当前边界之前时，不会在后续页重复；已经翻过边界的记录不因状态更新时间变化而换页。
- 权限校验、查询范围约束和字段脱敏必须在生成响应及游标之前完成。

## 2. 初始快照与 SSE

浏览器启动顺序：

1. 请求 `/admin/v1/me`，记录 `realtime.currentEventId`。
2. 分页读取当前可见的房间、玩家和实例快照。
3. 请求 `GET /admin/v1/events`，通过 `Last-Event-ID` 传入第 1 步水位。
4. 将收到的增量事件合并到当前页面；收到 `resync` 后重新执行 1–3。

事件 ID 格式：

```text
<admin-instance-id>:<monotonic-sequence>
```

事件类型：

- `room.upsert`、`room.remove`
- `player.upsert`、`player.remove`
- `instance.upsert`、`instance.remove`
- `overview.upsert`
- `resync`

事件数据：

```json
{
  "entityKey": "entity-id",
  "payload": {},
  "occurredAtUtc": "2026-07-29T00:00:00Z"
}
```

断线恢复规则：

- 当前实例仍保留 `Last-Event-ID` 之后的事件时，按序回放。
- 事件早于积压窗口、ID 损坏或重连到其他 Admin 副本时，返回 `resync`。
- 服务器每 15 秒写入 SSE 注释心跳；心跳不递增业务序列。
- 每订阅者队列有硬上限。慢消费者队列满时断开连接，由客户端按指数退避重连并恢复或重同步。
- 浏览器只在 `SseEnabled=false` 且 `LegacyPollingEnabled=true` 时启用 5 秒轮询兼容模式。

## 3. 容量与背压

默认容量保护：

- 房间：10,000
- 玩家：100,000
- Dedicated Server 实例：20,000
- SSE 积压：20,000 个事件
- 单订阅者队列：256 个事件
- 玩家后台扫描：每个快照周期最多 20 页

玩家增量检测采用跨周期滚动游标。完整扫描结束前不判定删除，首轮扫描只建立哈希基线，不向浏览器发送 10 万条 `upsert`。房间列表请求直接调用 Lobby 的数据库分页，不允许 Admin 为每个浏览器请求重新扫描一万房间。

## 4. 发布门禁

可复现环境：

```powershell
docker compose -f Deploy/capacity/compose.yaml up -d --build postgres redis auth lobby admin
$env:MONITORING_REQUESTS_PER_SECOND = "10"
$env:MONITORING_DURATION = "5m"
docker compose -f Deploy/capacity/compose.yaml --profile load run --rm k6
```

发布阈值：

- HTTP P95 < 750 ms
- HTTP P99 < 1,500 ms
- HTTP 错误率 < 1%
- 检查通过率 > 99%
- 所有响应页大小不超过 200

容量环境使用专用测试身份和测试限流值，不得把示例令牌或密码用于生产。
