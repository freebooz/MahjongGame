# Agones 专用游戏服务器部署

本目录提供可选的 Agones 生产部署骨架。现有 Lobby/Allocator 本地与 WSL 流程保持不变；只有显式传入
`-MahjongOrchestrator=Agones` 或环境变量 `MAHJONG_ORCHESTRATOR=agones` 时，UE 专用服务器才连接
Agones Sidecar。

## 前置条件

- Linux Server 包已输出到 `Artifacts/LinuxServer/`。
- Kubernetes 集群已安装 Agones，且已创建 `guiyang-mahjong` namespace。
- Agones 已启用 `PlayerTracking` feature gate；游戏服务器会把容量和在线玩家数同步到 GameServer 状态。
- 镜像使用不可变版本或 digest；禁止直接把清单中的占位值用于生产。

本地使用官方 YAML 安装 Agones 后，可按以下方式启用 PlayerTracking；生产集群建议在 Helm 安装值中
启用同一 feature gate，并使用安装时生成的唯一证书：

```bash
kubectl set env deployment/agones-controller deployment/agones-extensions deployment/agones-allocator \
  -n agones-system FEATURE_GATES=PlayerTracking=true
kubectl rollout status deployment/agones-controller -n agones-system
kubectl rollout status deployment/agones-extensions -n agones-system
kubectl rollout status deployment/agones-allocator -n agones-system
```

## 构建镜像

在仓库根目录执行：

```bash
docker build -f Deploy/Docker/Dockerfile.gameserver \
  -t ghcr.io/freebooz/guiyang-mahjong-server:<version> .
```

## 部署与分配

先将 Fleet 中的镜像和构建版本占位值替换为本次发布值，然后执行：

```bash
kubectl create namespace guiyang-mahjong
kubectl apply -f Deploy/Agones/guiyang-mahjong-sdk-rbac.yaml
kubectl apply -f Deploy/Agones/guiyang-mahjong-fleet.yaml
kubectl apply -f Deploy/Agones/guiyang-mahjong-autoscaler.yaml
kubectl get fleet,gameserver,fleetautoscaler -n guiyang-mahjong
```

将 `Allocator__Backend` 设为 `Agones` 后，Allocator 会通过集群内 Kubernetes API 创建
`GameServerAllocation`，写入唯一的房间、对局、服务器实例和一次性注册元数据，再把响应中的地址与
动态端口纳入原有持久化、注册、心跳和 Lobby 路由流程。`allocator-linux.yaml` 已包含最小 namespace
级 RBAC。

## 当前集成边界

Agones 适配器负责 SDK 连接、Ready、Allocation Watch、玩家计数和关闭通知。Lobby/Allocator 继续
负责房间事务、单活动房间约束、票据签发、服务注册及心跳。UE 只有在收到完整 Allocation 元数据并
成功向 Allocator 注册后才接受带签名加入票据的玩家。

## 验证

```bash
kubectl describe fleet guiyang-mahjong -n guiyang-mahjong
kubectl logs -n guiyang-mahjong -l agones.dev/fleet=guiyang-mahjong -c game-server --tail=200
kubectl create -f Deploy/Agones/guiyang-mahjong-allocation.yaml -o yaml
```

完成 Auth、Lobby、Allocator、PostgreSQL 和 Redis 部署后，可从 Windows PowerShell 执行端到端冒烟：

```powershell
.\Scripts\Test-KubernetesAgones.ps1
```

脚本使用 `28080-28082` 本地端口，避免与 WSL Compose 默认的 `18080-18082` 冲突，并在验证后释放测试房间。

在本地集群执行 200 客户端控制面负载和真实 UE Fleet 扩缩容验证：

```powershell
.\Scripts\Test-KubernetesAgonesScale.ps1 -ClientCount 200 -GameRoomCount 8
```

该脚本并发创建独立 Auth/Lobby 会话，并在本机容量范围内创建四人房间，等待 Buffer Autoscaler 补齐
Ready 容量后再判定成功。为了保持 Auth 的每 IP 每分钟 30 次生产限流，脚本会临时将 Auth 扩到 7 个
Pod 并分别建立本地入口，结束后恢复原副本数。完整 200 人同时在牌桌中需要 50 台 UE Dedicated Server，
应在具备相应 CPU、内存和 Pod 容量的多节点测试集群执行 `-GameRoomCount 50`。

健康状态以 Agones SDK Sidecar 收到的 UE health ping 为准；容器 TCP/HTTP 探针不能代表 UDP 游戏
进程已完成 Agones 注册。
