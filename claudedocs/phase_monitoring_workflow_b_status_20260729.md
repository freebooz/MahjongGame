# 工作流 B：Dedicated Server 遥测正确性执行与验收记录

> 执行日期：2026-07-29  
> 范围：MON-010～MON-014  
> 数据链路：UE Dedicated Server → Lobby → Admin  
> 契约版本：`telemetrySchemaVersion = 1`  
> 结论：实现与自动化验证完成；完整 Server 链接、运行时 OS 对照和流量/RPC 压测仍是发布门禁

## 1. 执行结论

本轮已经完成以下代码能力：

1. Dedicated Server 使用 UE 平台层上报进程 CPU、RSS/工作集和 CPU 采样窗口；
2. 使用 GameNetDriver 累加应用层入站/出站字节，并处理 `uint32` 回绕和驱动重建；
3. Lobby 使用相邻同实例心跳计算 B/s，计数器回退、实例变化或时间不前进时返回 `null`；
4. 玩家遥测增加托管状态与变化时间、连接变化时间、掉线开始、重连时间、受控掉线原因、状态序号和 EventId；
5. InMemory 和 Redis 事件存储按 EventId 幂等，Redis 使用 Lua 保证去重标记与列表追加原子完成；
6. RPC 增加固定方法白名单、收到/拒绝/失败/超时累计量，以及最近 256 个同步样本的 P95/P99；
7. 结算增加 `Calculating/Submitted/Accepted/Failed/Compensating/Completed` 只读投影、ResultSequence、SHA-256、提交/确认时间和安全失败摘要；
8. Lobby 成功持久化结果后权威写入 `Completed`，普通运营接口仍无修改对局结果能力；
9. Admin 房间详情已展示 CPU、RSS、应用网络累计与速率、RPC 分类、托管/掉线原因和结算状态。

## 2. 关键口径校准

### 2.1 进程内存

原审查报告把 `FPlatformMemory::GetStats().UsedPhysical` 判断为系统总已用内存。经核对 UE 5.8 源码：

- Windows 实现读取当前进程 `WorkingSetSize`；
- Linux 实现读取 `/proc/self/status` 的 `VmRSS`；
- 因此该值已经是 Dedicated Server 进程 RSS/工作集，不是节点总内存。

本轮保留正确采集方式，补充源码注释、契约定义和管理台名称，避免再次误判。

### 2.2 CPU

- 生产者使用 `FPlatformTime::GetCPUTime().CPUTimePct`；
- 该字段已按节点全部逻辑 CPU 总容量归一化，范围为 0～100；
- 不使用可能按单核超过 100% 的 `CPUTimePctRelative`；
- 当前 UE 平台采样窗口为 250 ms，并通过 `processCpuSampleWindowMilliseconds` 显式传递。

### 2.3 网络

- 当前统计范围为 GameNetDriver 应用层载荷；
- 不包含 IP/UDP/TCP 头，也不包含控制面 HTTP；
- 页面必须显示“应用网络”，不得误称为网卡或节点总流量；
- Lobby 只对同一实例、单调计数器和正采样间隔计算速率。

## 3. 代码落点

### Dedicated Server 与共享网络层

- `Source/GuiyangMahjong/Private/Game/GuiyangMahjongPlayerController.cpp`
- `Source/GuiyangMahjong/Public/Game/GuiyangMahjongPlayerController.h`
- `Source/GuiyangMahjongServer/Private/Server/GuiyangGameServerBridge.cpp`
- `Source/GuiyangMahjongServer/Public/Server/GuiyangGameServerBridge.h`
- `Source/GuiyangMahjongServer/Private/Game/GuiyangMahjongGameMode.cpp`
- `Source/GuiyangMahjongServer/Public/Game/GuiyangMahjongGameMode.h`
- `Source/GuiyangMahjongServer/Private/Room/GuiyangRoomManager.cpp`
- `Source/GuiyangMahjongServer/Public/Room/GuiyangRoomManager.h`

### Lobby、Admin 与契约

- `Services/GuiyangMahjong.Lobby/Domain/LobbyModels.cs`
- `Services/GuiyangMahjong.Lobby/Services/LobbyService.cs`
- `Services/GuiyangMahjong.Lobby/Storage/RoomMonitoringStore.cs`
- `Services/GuiyangMahjong.Admin/Domain/MonitoringModels.cs`
- `Services/GuiyangMahjong.Admin/wwwroot/app.js`
- `Contracts/OpenAPI/lobby-v1.openapi.yaml`
- `Contracts/Monitoring/runtime-telemetry-v1.md`

### 测试

- `Services/GuiyangMahjong.Lobby.Tests/RuntimeTelemetryContractTests.cs`
- `Services/GuiyangMahjong.Lobby.Tests/AllocatorIntegrationDomainTests.cs`
- `Services/GuiyangMahjong.Admin.Tests/RuntimeTelemetryWireContractTests.cs`
- `Source/GuiyangMahjongEditorTools/Private/Tests/GuiyangManagedGameServerTests.cpp`

## 4. 自动化验收

### 4.1 .NET

全套标准测试结果：

| 项目 | 通过 | 跳过 | 失败 |
|---|---:|---:|---:|
| Allocator | 20 | 0 | 0 |
| Auth | 11 | 4 | 0 |
| Lobby | 45 | 6 | 0 |
| PlayerData | 4 | 2 | 0 |
| Admin | 42 | 3 | 0 |
| 合计 | 122 | 15 | 0 |

跳过项均为需要显式外部 PostgreSQL/Redis 环境变量的集成测试，不是失败。

新增门禁覆盖：

- 全字段心跳线协议无损映射；
- CPU 范围和采样窗口；
- 网络速率与计数器重置抑制；
- 连接 EventId/Sequence 重复心跳去重；
- InMemory 并发 EventId 去重；
- Lobby/Admin 模型字段一致性；
- 显式结算投影在持久化后转为 Completed；
- OpenAPI 字段完整性。

### 4.2 契约与前端

- OpenAPI YAML 解析通过：20 个 schema，心跳 21 个顶层字段；
- Admin `app.js` 通过 `node --check`；
- `git diff --check` 无空白错误。

### 4.3 Unreal 编译

Windows `Win64 Development` 非 Unity 单文件编译：

- `GuiyangMahjongPlayerController.cpp`：通过；
- `GuiyangMahjongGameMode.cpp`：通过；
- `GuiyangRoomManager.cpp`：通过；
- `GuiyangGameServerBridge.cpp`：通过；
- `GuiyangManagedGameServerTests.cpp`：通过。

Linux `Development` 交叉编译：

- 上述四个生产代码编译单元全部通过。

同时修复了原有非 Unity 隐式依赖：

- PlayerController 显式包含 `Engine/World.h`；
- GameMode 和自动化测试显式包含 `Engine/GameInstance.h` 与引擎版本头。

## 5. 任务验收状态

| 任务 | 当前状态 | 尚需发布门禁 |
|---|---|---|
| MON-010 | 实现完成 | 用链接后的新二进制在 Windows/Linux 分别与任务管理器、`ps`/`/proc` 对照 RSS 和 CPU 误差 |
| MON-011 | 实现完成 | 使用可控 UDP 负载与系统工具对照趋势；确认页面累计量和 B/s |
| MON-012 | 实现完成，场景演练待门禁 | Kicked、ServerShutdown 仍需从真实管理链路触发并核对时间线 |
| MON-013 | 实现完成 | 执行单房间 RPC 风暴压测，验证报警阈值和 P95/P99 趋势 |
| MON-014 | 核心投影完成 | Compensating 尚需与审批通过的补偿工作流联动，且必须保持只读结果约束 |

## 6. 当前环境阻塞

完整 `GuiyangMahjongServer Win64 Development` 链接在当前源码引擎环境中触发 490 个引擎动作。
机器当时仅约 1.8 GB 可用物理内存；单并发编译在 10 分钟内仍停留在引擎模块，已终止本轮启动的 UBT/CL 进程，避免继续占用工作站。

限定 `GuiyangMahjong` 与 `GuiyangMahjongServer` 的模块构建已缩小到 14 个动作，但同样因换页长时间停滞。
因此：

- 不能声称已经生成包含本轮改动的新 Server 可执行文件；
- 不能使用 2026-07-22 的旧二进制冒充运行时验收；
- Windows/Linux 的源码交叉编译已经通过，但 OS 工具误差对照、网络负载和 RPC 风暴仍需在完整链接后执行。

建议在至少 48～64 GB 可用内存的构建机，或已有完整 UE Engine 缓存的 CI Agent 上执行最终链接。

## 7. 安全与回退

- 所有新增字段均为 v1 可选字段；旧 Dedicated Server 缺失字段时继续保留 `null`；
- 未知 `telemetrySchemaVersion` 仍在任何下游副作用前失败关闭；
- RPC 方法名来自代码常量，不含 PlayerId/RoomId/请求参数；
- 结算投影不包含修改结果接口，普通运营角色仍只能读取；
- ResultHash 为 SHA-256，只用于争议核对；
- EventId 去重不改变已有事件只追加语义；
- 回退旧生产者时，Lobby/Admin 继续接受缺失的新字段，不显示误导性零值。

## 8. 下一步执行顺序

1. 在高内存构建机完成 Win64 与 Linux Server 完整链接；
2. 启动新二进制，分别采集心跳与 OS 工具数据，完成 MON-010 误差表；
3. 运行可控网络负载，完成 MON-011 趋势对照；
4. 执行断线、重连、踢出、服务器关闭场景矩阵；
5. 执行 RPC 风暴压测并确定报警阈值；
6. 通过以上门禁后再把 MON-010、MON-011、MON-013 标记为“验收通过”。
