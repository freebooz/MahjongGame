# 贵阳麻将游戏解决方案

本仓库包含 Unreal Engine 客户端与 Dedicated Server、.NET 10 后端服务、Angular 22 管理端、部署清单、监控契约和美术源资产。仓库入口以源码和声明式配置为准，编译、发布、缓存产物不得作为源码提交。

## 目录入口

| 目录 | 职责 |
| --- | --- |
| `Source`、`Content`、`Config` | Unreal Engine C++、可发布资产与配置 |
| `SourceArt` | Blender、纹理、音频等可追溯美术源文件 |
| `Services` | .NET 服务、测试以及 Admin Angular 源码 |
| `Contracts` | OpenAPI、监控、容量、治理和 SLO 契约 |
| `Deploy` | Linux、Docker Compose、Kubernetes、Agones 与可观测性部署 |
| `Build` | Unreal 平台构建配置、目标规则及构建工具所需静态输入 |
| `Scripts` | 构建、部署、测试、资产生成/导入/验证入口 |
| `Docs` | 当前架构、设计、安全规范与运行手册 |
| `Artifacts` | 本地可重建的构建、打包、发布与验证产物 |
| `Evidence` | 需要跨构建保留、可关联需求或工单的长期审查证据 |
| `Saved` | Unreal 和本地工具产生的可删除缓存、日志与临时验证输出 |

## 常用命令

```powershell
# .NET 服务
dotnet restore .\Services\GuiyangMahjong.Services.slnx
dotnet build .\Services\GuiyangMahjong.Services.slnx -c Release
dotnet test .\Services\GuiyangMahjong.Services.slnx -c Release

# Angular 22 Admin
Push-Location .\Services\GuiyangMahjong.Admin\ClientApp
npm ci
npm run typecheck
npm run build
Pop-Location

# Windows 客户端与 Linux Dedicated Server
.\Scripts\Build-Client.ps1
.\Scripts\Build-LinuxServer.ps1
```

服务端生产入口与端口、密钥、回滚规则见 [Deploy/README.md](Deploy/README.md)。
架构、监控、安全规范和运行手册统一从 [核心文档导航](Docs/README.md) 进入。

## 仓库约束

- 修改代码时遵守根目录 `AGENTS.md` 的中文注释和前端技术栈策略。
- 更新美术资产必须精确删除目标旧资源后全量生成或导入，不使用覆盖导入保留旧设置。
- `Services/GuiyangMahjong.Admin/ClientApp` 是 Admin 前端唯一源码；`wwwroot` 是可重建发布目录。
- `Artifacts`、`Evidence`、`Saved` 分别承载可重建产物、长期证据和临时输出，不得混用。
- UE `.uasset/.umap` 的移动和重命名必须在 Unreal Editor 内完成并修复重定向器。
- 密钥、`.env`、数据库备份、诊断日志和本机缓存不得提交。
