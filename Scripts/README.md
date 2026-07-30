# 脚本目录治理

`Scripts` 保存项目自动化入口。现有根级脚本在迁移期继续兼容，新增能力应进入职责子目录并通过稳定入口调用。

## 分类

| 子目录 | 职责 |
| --- | --- |
| `lib` | PowerShell 公共环境解析与安全校验 |
| `Linux` | Linux/WSL 诊断、部署和测试 |
| `Load` | 容量与负载验证 |
| `Assets` | 后续迁入美术生成、导入、修复和验证脚本 |
| `Build` | 后续迁入客户端、服务端和包构建入口 |
| `Operations` | 后续迁入启动、部署、诊断和回滚入口 |
| `Tests` | 后续迁入契约、结构和集成门禁 |

## 编写约束

- 项目根目录从脚本/模块位置推导，允许显式参数覆盖；不得写入开发者盘符或个人临时目录。
- Unreal Engine 根目录优先使用显式参数，其次使用 `UE_ROOT`，并校验 `Build.bat` 或对应平台入口。
- 删除、覆盖、回滚和生产管理操作必须先校验目标位于预期工作区，并在帮助文本中说明副作用。
- 新增或修改代码必须补充准确中文注释，解释边界、失败条件和关键设计原因。
- 目录迁移必须保留兼容 shim，直到 CI、运行手册和外部调用方均完成切换。

结构门禁入口为 `Scripts/Test-ProjectStructureGovernance.ps1`。

## Docker 服务镜像矩阵

`Scripts/Linux/build-service-images.sh` 是服务镜像的本地统一验证入口：

- 默认构建 Auth、Lobby、PlayerData、Admin，以及包含真实
  `Artifacts/LinuxServer` 制品的 game-node 最终镜像；
- `--compile-only` 仍构建四个 Web 服务的最终镜像，但将 game-node 替换为
  Allocator 编译阶段，用于没有 Unreal LinuxServer 制品的服务 CI；
- `--tag <suffix>` 为本地镜像设置可追溯标签，`--no-cache` 用于验证完整冷构建；
- 该入口不会启动容器、推送镜像或改变部署状态。

CI 使用相同 Dockerfile 的独立矩阵项，确保单个服务失败不会隐藏其他镜像的结果。
