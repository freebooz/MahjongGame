# 房间运行遥测数据字典 v1

> 契约状态：冻结  
> 冻结日期：2026-07-29  
> 契约版本：`telemetrySchemaVersion = 1`  
> 数据链路：UE Dedicated Server → Lobby → Admin  
> 机器契约：`Contracts/Monitoring/runtime-telemetry-v1.schema.json`
> 相关 OpenAPI：`Contracts/OpenAPI/lobby-v1.openapi.yaml`

## 1. 版本与兼容规则

1. Dedicated Server 新构建必须显式发送 `telemetrySchemaVersion: 1`。
2. 为兼容已有构建，心跳缺失该字段时 Lobby 按 v1 解释。
3. 显式发送非 1 版本时，Lobby 在调用 Allocator 或写入监控存储前拒绝该心跳。
4. v1 允许新增可选、可空字段，但不得改变已有字段的单位、累计/瞬时语义和空值语义。
5. 字段单位、归一化方式、枚举含义或空值含义发生不兼容变化时，必须升级主版本。
6. Admin 的 `RoomRuntimeTelemetry` 线协议模型必须与 Lobby 同名模型保持字段名称、JSON 类型和默认值一致。

## 2. 统一语义

- 所有时间使用带时区的 ISO 8601 UTC。
- `null` 表示生产者没有观测到、当前平台不支持或当前构建尚未实现；不得自动转换为 `0`、`false` 或当前时间。
- 累计计数器以 Dedicated Server 进程启动为起点，进程重启后允许归零。
- 速率不由 Dedicated Server 心跳直接上报，应由相邻两个累计值及其 Lobby 接收时间计算。
- `ObservedAtUtc` 使用 Lobby 的接收时钟，是监控数据新鲜度的权威时间。
- `SentAtUtc` 使用 Dedicated Server 时钟，只用于诊断网络延迟和时钟漂移，不直接决定数据是否新鲜。
- PlayerId、RoomId、MatchId 和 ServerInstanceId 可以用于日志关联；不得将高基数玩家标识直接作为 Prometheus 全局标签。
- 心跳凭证、玩家令牌、完整 IP、密码和支付敏感数据不得进入遥测存储、日志或 Admin 返回。

## 3. 心跳公共字段

| 字段 | JSON 类型 | 必填 | 单位/格式 | 有效范围 | 语义与所有权 |
|---|---|---:|---|---|---|
| `telemetrySchemaVersion` | integer | 否 | 主版本 | 固定为 1 | 缺失按 v1；显式未知版本拒绝 |
| `roomId` | string | 是 | 不透明 ID | 1～80 字符 | Lobby 房间权威 ID |
| `heartbeatCredential` | string | 是 | 机密凭证 | ≥32 字符 | 当前实例专用，只在内部传输 |
| `connectedPlayers` | integer | 是 | 人数 | 0～4 | 当前仍由服务器会话管理的玩家数 |
| `connectedPlayerIds` | string[] | 否 | 玩家 ID | 最多 4 个且唯一 | 数量必须与 `connectedPlayers` 一致 |
| `roomLifecycle` | string | 是 | 枚举文本 | Waiting/Playing/Settling 等 | Dedicated Server 观察到的生命周期 |
| `roundId` | integer | 是 | 局序号 | ≥0 | 尚未开局为 0 |
| `buildVersion` | string | 是 | 构建版本 | 1～80 字符 | 用于兼容性、定位和回退 |
| `sentAtUtc` | string | 是 | ISO 8601 UTC | 有效时间 | Dedicated Server 发送时刻 |
| `gameStartedAtUtc` | string/null | 否 | ISO 8601 UTC | 有效时间 | 本场游戏首次开局时刻 |

## 4. 服务器指标

| 字段 | JSON 类型 | 单位 | 类型 | v1 有效范围 | v1 冻结语义 | 当前生产者状态 |
|---|---|---|---|---|---|---|
| `serverTickMilliseconds` | number/null | ms | 瞬时采样 | 0～10000 | 最近一次服务器世界 Tick 的耗时 | 已上报 |
| `serverFramesPerSecond` | number/null | frame/s | 瞬时采样 | 0～1000 | 最近一次 Tick 的倒数 | 已上报 |
| `rpcReceivedCount` | integer/null | 次 | 进程累计 | ≥0 | 进程启动以来收到的服务器 RPC 总数 | 已上报 |
| `processMemoryBytes` | integer/null | byte | 瞬时采样 | ≥0 | Dedicated Server 进程 RSS/工作集，不是系统总已用内存 | 已实现；Windows=`WorkingSetSize`，Linux=`VmRSS` |
| `processCpuPercent` | number/null | % | 区间采样 | 0～100 | 按节点全部逻辑 CPU 总容量归一化的进程占用 | 已实现 |
| `processCpuSampleWindowMilliseconds` | number/null | ms | 区间说明 | 250～60000 | CPU 数值所覆盖的最近采样窗口 | 已实现，当前为 250 ms |
| `networkIngressBytes` | integer/null | byte | 进程累计 | ≥0 | 进程启动以来 GameNetDriver 收到的应用层字节总数 | 已实现 |
| `networkEgressBytes` | integer/null | byte | 进程累计 | ≥0 | 进程启动以来 GameNetDriver 发送的应用层字节总数 | 已实现 |

### 4.1 指标展示约束

- `processMemoryBytes` 使用 UE 平台层的当前进程工作集：Windows 为 `WorkingSetSize`，Linux 为 `/proc/self/status` 的 `VmRSS`。
- CPU 不采用“每个逻辑核各 100%”的口径；如果采集 API 返回这种数值，生产者必须除以逻辑 CPU 数量后再发送。
- 网络速率计算必须处理计数器归零、进程重启、采样间隔为零和计数器回绕；出现这些情况时当前速率返回 null。
- 网络累计量是 UE GameNetDriver 的应用层载荷，不包含 IP/UDP/TCP 头和控制面 HTTP；Admin 必须以“应用网络”展示。
- `networkIngressBytesPerSecond` 与 `networkEgressBytesPerSecond` 由 Lobby 使用相邻有效样本计算，计数器回退或实例变化时为 null。
- `rpcMethods` 只允许源码固定方法名，最多 32 项；不得把 PlayerId、RoomId 或请求参数拼入方法名。
- RPC P95/P99 使用每个方法最近 256 次同步处理耗时；累计计数随进程重启归零。

## 5. 玩家运行字段

| 字段 | JSON 类型 | 必填 | 单位/格式 | 有效范围 | 语义 |
|---|---|---:|---|---|---|
| `playerId` | string | 是 | 不透明 ID | 1～80 字符 | Auth/Lobby 使用的玩家 ID |
| `seatIndex` | integer | 是 | 座位索引 | -1～3 | 未知座位为 -1 |
| `connectionState` | string | 是 | 枚举 | Connected/Disconnected/Reconnecting | 当前权威连接状态 |
| `latencyMilliseconds` | number/null | 否 | ms | 0～120000 | 最近一次服务端观测的玩家延迟 |
| `disconnectedAtUtc` | string/null | 否 | ISO 8601 UTC | 有效时间 | 当前掉线开始时刻；重连后清空 |
| `trustee` | boolean/null | 否 | 布尔 | true/false/null | 是否托管；null 表示生产者未知 |
| `trusteeChangedAtUtc` | string/null | 否 | ISO 8601 UTC | 有效时间 | 最近一次托管状态变化 |
| `connectionChangedAtUtc` | string/null | 否 | ISO 8601 UTC | 有效时间 | 最近一次连接状态变化 |
| `reconnectedAtUtc` | string/null | 否 | ISO 8601 UTC | 有效时间 | 最近一次成功重连 |
| `disconnectReason` | string/null | 否 | 受控枚举 | 5 种原因 | NormalExit/NetworkInterrupted/ReconnectTimeout/Kicked/ServerShutdown |
| `connectionStateSequence` | integer/null | 否 | 单调序号 | ≥0 | 房间内该玩家连接状态版本 |
| `connectionEventId` | string/null | 否 | UUID | 有效 UUID | 状态变化幂等键 |

### 5.1 玩家状态约束

- 新生产者的 `Disconnected` 或 `Reconnecting` 必须携带 `disconnectedAtUtc`、原因、序号和 EventId；旧构建仍允许字段缺失。
- `Connected` 状态的 `disconnectedAtUtc` 应为空。
- `trustee=null` 与 `trustee=false` 含义不同：前者未知，后者明确未托管。
- 玩家数组不得出现重复 PlayerId，不得超过房间最大人数。

## 6. Lobby 运行快照字段

Lobby 在接收和验证心跳后生成 `RoomRuntimeTelemetry`：

| 字段 | 来源 | 规则 |
|---|---|---|
| `roomId` | 心跳 | 必须对应已存在房间 |
| `serverInstanceId` | URL 路径 | 必须与房间当前路由实例一致 |
| `observedAtUtc` | Lobby 时钟 | 新鲜度权威时间，不信任发送端时钟 |
| `gameStartedAtUtc` | 当前心跳/上一快照 | 当前缺失时保留上一有效值 |
| `lifecycle` | 心跳 | 只通过房间状态机更新权威房间状态 |
| `currentRound` | 心跳 | 原样保存 |
| 服务器指标 | 心跳 | 校验范围后原样保存；null 原样保留 |
| `players` | 心跳或兼容推导 | 新版使用 `players`；旧版可从 `connectedPlayerIds` 生成最小快照 |
| `telemetrySchemaVersion` | 心跳 | v1 固定为 1 |
| 网络每秒速率 | Lobby 计算 | 仅同实例、计数器单调且采样时间前进时有值 |
| `rpcMethods` | 心跳 | 固定白名单、有界数量，校验累计量和分位数 |
| `settlement` | 心跳/Lobby 确认 | DS 提供 Calculating/Submitted/Accepted/Failed；Lobby 持久化后权威转为 Completed |

## 7. v1 兼容示例

### 7.1 当前完整 v1 载荷

```json
{
  "telemetrySchemaVersion": 1,
  "roomId": "room-example",
  "heartbeatCredential": "<internal-secret>",
  "connectedPlayers": 1,
  "connectedPlayerIds": ["player-1"],
  "roomLifecycle": "Playing",
  "roundId": 2,
  "buildVersion": "build-20260729",
  "sentAtUtc": "2026-07-29T01:30:00Z",
  "gameStartedAtUtc": "2026-07-29T01:25:00Z",
  "serverTickMilliseconds": 16.67,
  "serverFramesPerSecond": 59.98,
  "rpcReceivedCount": 1200,
  "processMemoryBytes": 268435456,
  "processCpuPercent": 12.5,
  "processCpuSampleWindowMilliseconds": 250,
  "networkIngressBytes": 9876,
  "networkEgressBytes": 5432,
  "rpcMethods": [
    {
      "methodName": "Server.RequestAction",
      "receivedCount": 100,
      "rejectedCount": 2,
      "failedCount": 1,
      "timeoutCount": 0,
      "p95DurationMilliseconds": 3.5,
      "p99DurationMilliseconds": 8.2
    }
  ],
  "players": [
    {
      "playerId": "player-1",
      "seatIndex": 0,
      "connectionState": "Connected",
      "latencyMilliseconds": 28,
      "disconnectedAtUtc": null,
      "trustee": false,
      "trusteeChangedAtUtc": "2026-07-29T01:26:00Z",
      "connectionChangedAtUtc": "2026-07-29T01:25:30Z",
      "reconnectedAtUtc": "2026-07-29T01:25:30Z",
      "disconnectReason": null,
      "connectionStateSequence": 3,
      "connectionEventId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
    }
  ],
  "settlement": null
}
```

### 7.2 旧构建兼容载荷

以下载荷缺少版本和全部可选指标，仍按 v1 接受；Admin 必须显示“未知”，不能显示零：

```json
{
  "roomId": "room-legacy",
  "heartbeatCredential": "<internal-secret>",
  "connectedPlayers": 0,
  "roomLifecycle": "Waiting",
  "roundId": 0,
  "buildVersion": "legacy-build",
  "sentAtUtc": "2026-07-29T01:30:00Z"
}
```

## 8. 契约变更门禁

任何涉及本数据字典的变更必须同时完成：

1. 更新本数据字典和 OpenAPI；
2. 更新 Lobby 与 Admin 的线协议模型；
3. 更新 Dedicated Server 生产者；
4. 更新端到端契约测试；
5. 提供滚动升级兼容和回退说明；
6. 同步维护相关代码中文注释；
7. 证明未把 null 自动转换为零值；
8. 对不兼容语义升级 `telemetrySchemaVersion`。
