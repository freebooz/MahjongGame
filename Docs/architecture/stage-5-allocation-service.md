# 阶段 5：统一 Allocation Service 实施与验收报告

## 1. 阶段结论

现有 `GuiyangMahjong.Allocator` 已在保持单部署单元、旧 HTTP 路径和 LobbyControl 调用方式兼容的前提下，
建立 `IGameServerProvider` 统一边界。`LocalProcessGameServerProvider` 与
`AgonesGameServerProvider` 使用同一生命周期契约，LobbyControl 和 Admin 均不感知、也不能直接调用具体 Provider。

分配租约现在持久化 `allocation_id`、幂等键、无敏感数据请求指纹、Provider、`room_epoch` 与显式
`fencing_token`。Allocation Service 在单节点串行事务和原子状态文件中执行唯一性检查，Lobby 的
`room.room_allocations` 继续作为房间到分配历史的 PostgreSQL 业务权威。Redis 未参与分配正确性。

## 2. 实施前真实状态

- 已有原子端口池、子进程启动、PID 与启动时间核验、状态文件、注册/心跳超时和崩溃检测。
- 已有排空、管理员终止、Agones Allocation REST 调用和最小权限 ServiceAccount。
- `GameServerInstanceManager` 同时直接依赖本地进程启动器和 Agones 客户端，Provider 边界不明确。
- 分配请求只有房间、比赛、Build 与 Epoch；幂等主要依赖活动 `room+epoch`。
- RoomEpoch 已贯通 Lobby、Allocator 与 DS，但租约没有独立 Fencing 投影。
- 状态恢复只能发现记录内 PID 已消失，不能反向报告状态文件之外的疑似孤儿进程。
- Agones Selector 只选择 Fleet 和游戏类型，没有地域、Build、RuleSet、协议与容量条件。
- Lobby 和 Admin 已只调用 Allocator HTTP，不直接持有 Kubernetes 或本地进程权限。

## 3. 本阶段不处理

- 不修改麻将出牌、胡牌、洗牌或计分规则。
- 不修改最终结算、玩家资产或回放证据所有权。
- 不引入 Redis 锁、NATS 或新的业务微服务。
- 不让 LobbyControl、Admin 或 Dedicated Server 创建其他 DS。
- 不把实时手牌或玩家隐私数据写入 Allocation Service。
- 不拆分现有 Allocator 部署单元。

## 4. 统一 Provider 架构

```text
LobbyControl / Admin / Dedicated Server
                 |
                 v
      Allocation Service HTTP/API
                 |
      GameServerInstanceManager
        幂等 + 唯一性 + Fencing
                 |
        IGameServerProvider
          /               \
 LocalProcess          Agones
 端口/进程/PID       Fleet/Allocation/GameServer
```

`IGameServerProvider` 的实际接口包括：

| 方法 | 语义 |
|---|---|
| `AllocateAsync` | 创建本地进程或 Agones GameServer，并返回内部运行句柄 |
| `GetStatusAsync` | 查询底层进程或 GameServer 状态，不改变生命周期 |
| `DrainAsync` | 优雅排空并终止 |
| `TerminateAsync` | 强制终止异常实例 |
| `RenewLeaseAsync` | 在上层 Fencing 校验后续租 |
| `ReportReadyAsync` | 在上层 Fencing 校验后确认 Ready |
| `ReportUnhealthyAsync` | 关闭已确认不健康资源 |
| `RecoverAsync` | 重启后核对并接管持久化资源 |
| `FindOrphanedAsync` | 反向报告状态文件之外的疑似孤儿进程 |
| `CheckReadyAsync` | 检查 Provider 身份、依赖和容量 |

所有异步接口均传播 `CancellationToken`。

## 5. 分配契约

规范分配输入新增：

```text
allocation_id
room_id
room_epoch
game_type
region
server_build
ruleset_version
protocol_version
requested_capacity
idempotency_key
```

旧 `matchId` 与 `buildVersion` 字段继续保留。旧调用方未提供 `allocationId` 或 `IdempotencyKey` 时，
兼容回退到 `X-Request-Id`。LobbyControl 已开始显式发送完整字段，但仍只调用统一
`POST /internal/allocations`。

新增只读恢复入口：

```text
GET /internal/allocations/{allocationId}
```

它用于分配成功但调用方没有收到 HTTP 响应时查询稳定结果，不会创建新实例。

## 6. 幂等、唯一性与 Fencing

Allocation Service 对以下键执行持久化唯一性校验：

- `allocation_id`；
- `idempotency_key`；
- `room_id + room_epoch`。

相同三组键及相同请求指纹返回原分配；任何键复用但参数变化返回 409。状态文件恢复时重新扫描全部记录，
发现重复键会拒绝就绪，不选择任意记录继续运行。

`FencingToken` 当前等于 `RoomEpoch`，但作为独立字段持久化和传输，便于后续独立演进。它已贯通：

```text
Allocator 租约
  -> LocalProcess -LeaseFencingToken
  -> Agones mahjong.freebooz/fencing-token
  -> UE DS 注册
  -> Lobby 转发
  -> Allocator Ready 校验
  -> UE DS 心跳
  -> Allocator RenewLease 校验
```

房间出现更高 Epoch 后，即使旧实例仍持有有效凭据，旧 Ready 和旧心跳也会返回 409。新 Epoch 被终止后，
旧 Epoch 仍不能重新成为当前租约。

## 7. LocalProcess Provider

本地 Provider 已实现：

- 端口池内原子租用和失败补偿释放；
- 固定配置可执行文件启动，HTTP 请求不能指定任意程序或参数；
- 启动硬超时与进程立即退出检查；
- PID 与进程启动时间联合核验，防止 PID 复用误接管；
- stdout/stderr 异步排空，避免管道阻塞；日志只记录流类型和字符数，不记录可能包含手牌或凭据的正文；
- Ready/注册超时、心跳超时和进程崩溃检测；
- 优雅终止和零宽限强制终止；
- 端口幂等释放；
- 服务重启后重新预留端口并接管进程；
- 反向扫描同一配置可执行文件，报告疑似孤儿 PID，不自动误杀。

## 8. Agones Provider

Agones Provider 使用命名空间内 ServiceAccount 调用：

- `GameServerAllocation create`；
- `Fleet get`；
- `GameServer get/delete`。

Selector 现在同时约束：

```text
Fleet
game_type
region
server_build
ruleset_version
protocol_version
requested_capacity
```

无兼容 Ready 容量映射为 503；Provider 超时映射为 504；返回名称、地址或端口不完整时拒绝分配。
Drain、Terminate 和 Unhealthy 最终都通过幂等 GameServer Shutdown/Delete 回收实例。

Kubernetes Linux 部署改用不包含 DS 二进制的 `allocator-agones` 镜像，取消 Allocator Pod 的
`hostNetwork`；实际 UDP 游戏流量仍由 Agones GameServer Pod 承载，不经过 Allocation Service。

## 9. 权限边界

- 只有 Allocation Service 项目引用 Agones 客户端并启动本地 DS 进程。
- LobbyControl 只持有 Allocation Service 的普通服务凭据。
- Admin 只持有审批后终止命令凭据，不持有 Kubernetes 身份。
- 监控只读凭据不能执行分配、排空或终止。
- LocalProcess 可执行路径只来自启动配置，API 不接收可执行文件路径或任意参数。
- Agones ServiceAccount 权限限定在目标命名空间和必要资源/动词。
- 架构测试阻止 Lobby、Admin、Auth 和 PlayerData 引入 Agones API 或 `Process.Start`。

## 10. 数据、Redis 与事件

本阶段没有新增 PostgreSQL 表或迁移：

- 房间到 DS 的业务绑定和历史仍由阶段 4 `room.room_allocations` 权威保存；
- Allocator JSON 文件只保存本节点 PID、端口、Agones 资源名、凭据哈希和可恢复运行租约；
- 文件继续使用临时文件、原子替换、`WriteThrough` 和 Linux 目录 `fsync`；
- 状态根版本保持 `SchemaVersion=1`，新字段均为追加可选字段，阶段 4 镜像回滚时会忽略未知字段。

Redis 未新增键，也不参与唯一性、租约或 Fencing。没有新增消息事件；实例失败仍通过现有 Lobby 回调重试。

## 11. 配置

Provider 选择：

```text
Allocator__Backend=LocalProcess
Allocator__Backend=Agones
```

新增配置：

```text
Allocator__StartupTimeoutSeconds=30
Allocator__AllowLegacyInitialFencingToken=true
Allocator__ValidateOperatingSystemPortAvailability=true
```

Lobby 调度约束：

```text
Lobby__Allocator__GameType
Lobby__Allocator__Region
Lobby__Allocator__GameServerBuildVersion
Lobby__Allocator__RuleSetVersion
Lobby__Allocator__ProtocolVersion
Lobby__Allocator__RequestedCapacity
```

所有 DS 完成 Fencing 字段滚动升级并超过最长旧实例生命周期后，应设置：

```text
Allocator__AllowLegacyInitialFencingToken=false
```

同一活动状态文件不能以另一 Provider 模式启动；恢复时发现模式不一致会拒绝就绪。

## 12. API 兼容与回滚

- 旧分配、实例、注册、心跳、排空和 Admin 终止路径全部保留。
- 新请求字段均有兼容默认值；旧响应字段未删除或改名。
- 新响应追加 `allocationId` 和 `fencingToken`，旧 JSON 客户端可以忽略。
- Provider 类型不进入 LobbyControl 决策；切换只通过 Allocation Service 部署配置完成。

回滚步骤：

1. 禁止新的房间分配与重新分配。
2. 等待活动实例完成，或通过审批排空/终止。
3. 确认没有仅依赖新版 Fencing 字段的新 DS 仍在运行。
4. 回退 Lobby、Allocator 和 DS 镜像到阶段 4 版本。
5. 保留状态文件；阶段 4 反序列化会忽略追加字段。
6. 验证分配、注册、心跳、重连、排空和结算链路。

## 13. 验证结果

已完成：

- `dotnet restore`：通过。
- 全解决方案编译：0 警告、0 错误。
- 全解决方案测试：231 通过、23 条件跳过、0 失败。
- Allocator 测试：36/36 通过。
- Lobby 测试：60 通过、8 条件跳过、0 失败。
- 架构测试：9/9 通过。
- LocalProcess 正常启动、启动超时、端口冲突补偿、立即崩溃、Ready 超时、心跳超时、优雅排空、强制终止、恢复和孤儿检测测试通过。
- Agones 成功、无容量和请求超时模拟测试通过。
- 响应丢失查询、幂等冲突、旧 Epoch、旧 Ready 与旧心跳 Fencing 测试通过。
- Linux Compose 和 Observability Compose 配置展开通过。
- Kubernetes/Agones 共 32 个 YAML 对象离线解析通过。
- Allocator Agones Docker 镜像实际构建通过。
- OpenAPI 3.1 YAML 解析通过。

受外部环境阻塞：

- 当前 UE 5.8 安装仍缺少 `Engine/Build/BatchFiles/Build.bat` 和 `RunUAT.bat`，无法执行
  `GuiyangMahjongServer` 编译及 Unreal 自动化测试；仅完成源码传播与架构静态检查。
- `Artifacts/LinuxServer` 不存在，因此包含真实 Dedicated Server 制品的 LocalProcess Docker 镜像无法构建和启动。
- 当前没有可访问 Kubernetes/Agones 集群，无法执行真实 GameServerAllocation 联机验收；本阶段完成客户端模拟、
  RBAC/清单离线检查和 Agones Allocator 镜像构建。
- 仓库没有 Helm Chart，本阶段无 Helm 模板可验证。

## 14. 阶段验收

统一 Provider、Lobby 隔离、幂等、Fencing、Local/Agones 单元与模拟集成、Compose 配置、镜像和清单验证均满足阶段 5 核心目标。
完整运行验收状态为“有条件通过”：恢复完整 UE 5.8 工具链并生成 LinuxServer 制品后，需要执行 LocalProcess
Compose 实例启动；获得测试 Agones 集群后，需要执行真实 Allocation、Ready、Drain 和 Shutdown 验证。

## 15. 实际修改文件

### Allocation Service

- `Services/GuiyangMahjong.Allocator/Providers/GameServerProviderContracts.cs`
- `Services/GuiyangMahjong.Allocator/Providers/LocalProcessGameServerProvider.cs`
- `Services/GuiyangMahjong.Allocator/Providers/AgonesGameServerProvider.cs`
- `Services/GuiyangMahjong.Allocator/Domain/AllocatorModels.cs`
- `Services/GuiyangMahjong.Allocator/Domain/GameServerProcessContracts.cs`
- `Services/GuiyangMahjong.Allocator/Services/GameServerInstanceManager.cs`
- `Services/GuiyangMahjong.Allocator/Services/GameServerProcessLauncher.cs`
- `Services/GuiyangMahjong.Allocator/Services/AgonesAllocationClient.cs`
- `Services/GuiyangMahjong.Allocator/Services/AllocatorStateStore.cs`
- `Services/GuiyangMahjong.Allocator/Api/AllocatorEndpoints.cs`
- `Services/GuiyangMahjong.Allocator/Api/AllocatorExceptionMiddleware.cs`
- `Services/GuiyangMahjong.Allocator/Options/AllocatorOptions.cs`
- `Services/GuiyangMahjong.Allocator/Program.cs`
- `Services/GuiyangMahjong.Allocator/appsettings.json`

### Lobby、UE 与契约

- `Services/GuiyangMahjong.Lobby/Options/LobbyOptions.cs`
- `Services/GuiyangMahjong.Lobby/Services/AllocatorClient.cs`
- `Services/GuiyangMahjong.Lobby/Services/LobbyService.GameServers.cs`
- `Services/GuiyangMahjong.Lobby/Domain/LobbyModels.cs`
- `Services/GuiyangMahjong.Lobby/appsettings.json`
- `Contracts/OpenAPI/allocator-v1.openapi.yaml`
- `Source/GuiyangMahjongServer/Public/Server/GuiyangGameServerBridge.h`
- `Source/GuiyangMahjongServer/Private/Server/GuiyangGameServerBridge.cpp`
- `Source/GuiyangMahjongServer/Private/Server/GuiyangAgonesLifecycleSubsystem.cpp`

### 部署与验证

- `Deploy/linux/compose.yaml`
- `Deploy/kubernetes/namespace-and-config.yaml`
- `Deploy/kubernetes/allocator-linux.yaml`
- `Deploy/Agones/guiyang-mahjong-fleet.yaml`
- `Deploy/Agones/guiyang-mahjong-allocation.yaml`
- `Services/GuiyangMahjong.Allocator.Tests/GameServerInstanceManagerTests.cs`
- `Services/GuiyangMahjong.Lobby.Tests/AllocatorIntegrationDomainTests.cs`
- `Services/GuiyangMahjong.Architecture.Tests/ProjectArchitectureTests.cs`
- `Source/GuiyangMahjongEditorTools/Private/Tests/GuiyangManagedGameServerTests.cpp`
- `Services/GuiyangMahjong.Observability/MahjongTelemetry.cs`

本清单不包含阶段 5 开始前已存在的阶段 3/4 未提交修改；它们均被保留且未回退。
