# 贵阳捉鸡麻将服务端部署

服务端统一部署到 Linux。Windows 仅用于 UE 交叉编译和通过 WSL2 管理本地 Linux 环境；Auth、Lobby、Allocator、PostgreSQL、Redis 与 UE Dedicated Server 均不作为 Windows 服务运行。

## 部署入口分级

| 入口 | 状态 | 用途与约束 |
| --- | --- | --- |
| `Deploy/linux/deploy.sh` | Current / Production | Linux 唯一生产安装、升级、状态、备份和回滚入口 |
| `Deploy/linux/compose.yaml` | Current / Production | Linux 服务编排基线，密钥来自未跟踪 `.env` |
| `Deploy/observability/compose.yaml` | Current | Prometheus、Loki、Tempo、Grafana 可观测性栈 |
| `Deploy/kubernetes`、Agones 清单 | Current / Cluster | 多集群与游戏服编排；发布前执行治理契约门禁 |
| `Deploy/docker-compose.yml` | Compatibility | 旧本地入口，仅用于兼容验证，不得作为新生产部署依据 |
| Windows Allocator 容器清单 | Historical | 只保留回溯，生产环境禁止使用 |

所有新部署文档和自动化必须指向 Current 入口。兼容/历史入口的变更不得引入第二套密钥、包版本或数据库迁移事实来源。

## 推荐环境

- Ubuntu 22.04 LTS x86_64（原生服务器、虚拟机或 WSL2）
- Docker Engine 及 Compose v2
- 至少 4 CPU、8 GiB 内存；运行真实 UE 游戏服时建议 16 GiB 以上
- 对客户端开放 `18082/TCP`（Auth）、`18080/TCP`（Lobby/WebSocket）和 `19000-19099/UDP`（游戏实例）
- `18081/TCP` 是 Allocator 运维端口，生产环境应只允许可信管理网访问

持久数据位于 `/var/lib/guiyang-mahjong` 和 Docker 命名卷。不要把数据库、Allocator state 或 outbox 放在 WSL 的 `/mnt/c`、`/mnt/d`、`/mnt/h` 下。

## 一键安装

在原生 Linux 或 WSL2 的 Linux 文件系统内克隆/同步仓库，然后执行：

```bash
cd ~/src/MahjongGame
sudo ./Deploy/linux/deploy.sh install --bootstrap --version 2026.07.20-1
```

`--bootstrap` 仅在首次安装时使用，它会安装 Docker Engine/Compose 和基础诊断工具。首次运行会创建权限为 `0600` 的 `Deploy/linux/.env`，自动生成随机密钥并探测 advertised IPv4。部署后务必核对该地址确实可被 PC、手机和平板访问：

```bash
sudo ./Deploy/linux/deploy.sh status
sudo ./Deploy/linux/deploy.sh doctor
sudo ./Scripts/Linux/diagnose-network.sh
```

在 Windows 中调用同一个入口：

```powershell
wsl -d Ubuntu-22.04 -- bash -lc "cd ~/src/MahjongGame && sudo ./Deploy/linux/deploy.sh install --version 2026.07.20-1"
```

当前 WSL 建议使用 `%UserProfile%\.wslconfig`：

```ini
[wsl2]
networkingMode=mirrored
dnsTunneling=true
firewall=true
autoProxy=true
vmIdleTimeout=86400000
```

`vmIdleTimeout` 设为 24 小时，避免仅由 systemd/Docker 承载服务时 WSL 使用默认 60 秒空闲回收。修改后运行 `wsl --shutdown` 再启动发行版。Windows 与 Linux 两侧诊断可运行：

```powershell
.\Scripts\Diagnose-WslNetwork.ps1
```

为了让 WSL 本地验收栈在没有终端窗口时保持运行，可把仓库提供的启动脚本注册为当前用户登录任务：

```powershell
$projectRoot = (Resolve-Path '.').Path
$startupScript = Join-Path $projectRoot 'Scripts\Start-WslServerStack.ps1'
$action = New-ScheduledTaskAction -Execute powershell.exe -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$startupScript`""
$trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
Register-ScheduledTask -TaskName GuiyangMahjong-WSL-Server -Action $action -Trigger $trigger -Force
```

脚本只负责保持 WSL 实例存活、等待 Linux Docker daemon 并运行真实 `status`；它不会在 Windows 中启动任何业务服务。本机已注册的同名任务日志位于 `%LOCALAPPDATA%\GuiyangMahjong\wsl-startup.log`。

若 Hyper-V 防火墙默认阻止局域网入站，请在提升权限的 PowerShell 中只开放客户端必需端口（Allocator 18081 保持内部访问）：

```powershell
.\Scripts\Configure-WslFirewall.ps1 -RemoteAddresses LocalSubnet
```

## UE 游戏服

game-node 只接受 `Artifacts/LinuxServer` 中已 Cook/Stage 的真实 UE LinuxServer。
仓库和部署入口不再提供模拟 GameServer；缺少真实产物时镜像构建会直接失败。

Windows 构建机先安装 UE 5.8 v26 Linux 交叉工具链并设置 `LINUX_MULTIARCH_ROOT`，然后执行：

```powershell
.\Scripts\Build-LinuxServer.ps1 -EngineRoot $env:UE_ROOT -Configuration Shipping
```

构建输出为 `Artifacts/LinuxServer`，包含 `build-manifest.json`。将完整目录同步到 Linux 仓库后升级：

```bash
sudo ./Deploy/linux/deploy.sh upgrade --version <immutable-version>
```

部署产物默认排除 `.debug`/`.sym`，以减小同步和运行镜像体积；完整调试符号仍保留在 `Saved/StagedBuilds/LinuxServerArchive`。如确实需要把符号装入产物，可给构建脚本增加 `-IncludeDebugSymbols`。

在本机 WSL 环境可用一个命令完成产物校验、同步、variant 切换、镜像构建和升级：

```powershell
$env:MAHJONG_LINUX_REPOSITORY_PATH = '/srv/guiyang-mahjong'
.\Scripts\Deploy-LinuxServerToWsl.ps1 -Configuration Shipping -Version <immutable-version>
```

该入口会同步服务端源码、契约、Linux 脚本、部署清单和 UE 产物，但保留 WSL 中权限为 root-only 的 `.env` 以及部署版本状态文件，避免 Windows 工作区覆盖服务器密钥。

部署脚本在所有 readiness 通过后执行真实建房/分配/释放冒烟。只有 Lobby 能成功获得 advertised game endpoint 才会报告 `DEPLOYMENT_OK`。

## 日常运维

```bash
# 查看服务和真实 readiness
sudo ./Deploy/linux/deploy.sh status

# 升级到新镜像/本地产物
sudo ./Deploy/linux/deploy.sh upgrade --version 2026.07.21-1

# 部署前手工备份 PostgreSQL、Allocator state/outbox
sudo ./Deploy/linux/deploy.sh backup

# 回滚应用镜像版本（不删除数据卷）
sudo ./Deploy/linux/deploy.sh rollback

# 明确确认后恢复指定备份
sudo ./Deploy/linux/deploy.sh restore --backup-file /path/to/backup.tar.gz --confirm
```

失败诊断会写入 `Deploy/linux/diagnostics`。不要提交 `.env`、备份、诊断日志或真实密钥。升级/回滚不会执行 `docker compose down --volumes`，不得手工删除 PostgreSQL、Redis 或 Allocator 数据卷。

## 架构与验收资料

<<<<<<< HEAD
- [当前系统与模块清单](../Docs/architecture/current-system-inventory.md)
- [当前主业务流程](../Docs/architecture/current-main-flows.md)
- [统一 Allocation Service](../Docs/architecture/stage-5-allocation-service.md)
- [Dedicated Server 生命周期与恢复](../Docs/architecture/stage-6-dedicated-server-lifecycle-recovery.md)
=======
- [现行平台架构文档索引](../Docs/README.md)
- [Allocation Service 与 Agones 边界](../Docs/architecture/stage-5-allocation-service.md)
- [配置、灰度与多版本治理](../Docs/architecture/stage-11-configuration-release-governance.md)
>>>>>>> ff4853bbd5831cc9697d440b00feb887168a2425
- [告警运行手册](../Docs/RUNBOOKS/OBSERVABILITY_ALERTS.md)
- [多集群与 SLO 治理运行手册](../Docs/RUNBOOKS/SLO_MULTI_CLUSTER_GOVERNANCE.md)

旧的 `Deploy/docker-compose.yml` 和 Windows Kubernetes Allocator 清单只保留作历史兼容；新部署必须使用 `Deploy/linux/compose.yaml`。

Docker Desktop/WSL 的 Kubernetes 集成验收可额外应用
`Deploy/kubernetes/local-dev-dependencies.yaml`。该清单仅提供本地 PostgreSQL/Redis，生产集群仍必须使用
托管存储、外部 Secret 和不可变镜像摘要。
