# 当前严格配置与工具数据字典

> 本文档是严格 JSON、Unreal 工程配置、Grafana 仪表盘和生成清单的当前维护入口。
> 这些文件由工具直接解析，不能为了添加说明而插入非标准注释；职责、覆盖顺序和安全约束统一登记在此。

## 1. Unreal 工程与插件

| 文件 | 职责与关键字段 | 约束 |
|---|---|---|
| `GuiyangMahjong.uproject` | `EngineAssociation` 指定 Unreal 版本；`Modules` 和 `Plugins` 定义运行时、客户端、服务端及编辑器边界。 | Client 不得依赖 Server/Editor；引擎或模块变化后必须从同一源码重建 Client 与 Linux Dedicated Server。 |
| `Plugins/Agones/Agones.uplugin` | 声明 Agones 插件版本、模块类型、加载阶段和平台支持。 | 插件版本必须与源码 API 一致；Dedicated Server 只能引入 Runtime 模块。 |

## 2. .NET 服务配置

| 文件组 | 职责 | 生命周期与安全约束 |
|---|---|---|
| `Services/GuiyangMahjong.*/appsettings*.json` | 各服务数据库、Redis、NATS、内部身份、限流、遥测和服务发现配置。 | 基础文件只保存无敏感默认值；生产密钥、连接串和凭据必须由环境变量或 Secret 注入，缺失时启动失败。 |
| `Services/global.json` | 固定 .NET SDK 主版本和 roll-forward 行为。 | CI、开发机和容器必须使用兼容 SDK；升级前执行全解决方案还原、编译、测试和镜像矩阵。 |

ASP.NET Core 配置覆盖顺序为：基础 `appsettings.json` → 环境文件 → 环境变量 → 命令行。
嵌套键使用双下划线，例如 `Lobby__MonitoringReadOnlyToken`；生产环境不得依赖 Development 文件。

## 3. Angular 管理端

| 文件 | 职责 | 约束 |
|---|---|---|
| `ClientApp/angular.json` | 构建入口、样式、资产、预算和输出目录。 | 源码固定在 Admin `ClientApp`，生产输出固定到 `wwwroot`；不得手工修改构建产物。 |
| `ClientApp/package.json` | Angular 22、TypeScript、构建与类型检查脚本。 | 不引入其他生产前端框架；依赖变化必须通过 `npm ci`、类型检查和生产构建。 |
| `ClientApp/package-lock.json` | npm 完整依赖图和完整性哈希。 | 只能由受信 npm 版本生成，不得手工编辑。 |
| `ClientApp/tsconfig.json`、`tsconfig.app.json` | TypeScript 严格模式、目标、模块解析和应用范围。 | 不得关闭严格检查或通过包含生成目录规避错误。 |
| `ClientApp/proxy.conf.json` | 本地 API 与 WebSocket 代理。 | 仅限开发，不得包含生产地址、令牌或绕过身份/TLS 的规则。 |

## 4. 契约与 Grafana

| 文件组 | 职责 | 约束 |
|---|---|---|
| `Contracts/Authentication/player-access-token-v1.contract.json` | 玩家访问令牌算法、声明、时间和标识格式。 | Auth、EdgeGateway、Lobby 和 DS 必须共同兼容；弱化算法或声明必须升级契约版本。 |
| `Contracts/Monitoring/runtime-telemetry-v1.schema.json` | 运行遥测字段、单位、枚举和范围。 | DS、Workers 与 Admin 聚合器共同遵守，禁止用玩家或房间标识生成无限基数指标名。 |
| `Deploy/observability/grafana/dashboards/*.json` | Dashboard UID、变量、查询、阈值、数据源和跳转。 | 查询不得暴露玩家敏感字段；导出后仍需验证稳定 UID、高基数和数据源引用。 |

## 5. 美术生成清单

| 文件组 | 职责 | 约束 |
|---|---|---|
| `SourceArt/3D/MahjongTableMobileProduction/*Manifest.json` | 移动端麻将桌源文件、网格、纹理、尺寸、材质槽、哈希和生成版本。 | 由确定性脚本生成；更新前精确删除目标旧资产，再全量生成和导入。 |
| `SourceArt/3D/MahjongTableProduction/*Manifest.json` | 桌面端麻将桌 PBR 源、纹理、导出结果与校验信息。 | 清单必须与文件 SHA 一致；生产版与审查版不得混用依赖。 |
| `SourceArt/UI/Data/ui_asset_inventory.json` | UI 逻辑名、源文件、Unreal 目标路径、尺寸和用途。 | UI 导入以清单为事实来源；删除或重命名前必须检查 Widget 与地图引用。 |

## 6. 通用约束

1. 严格 JSON 使用 UTF-8，禁止尾随逗号和非标准注释。
2. 配置与清单不得保存密钥、真实账号、完整 IP、支付敏感数据或本机绝对路径。
3. 人工修改后必须执行 JSON 解析、对应框架构建和契约门禁。
4. 生成清单只能由声明脚本更新，并校验产物哈希。
5. 新增严格配置时必须同步登记职责、覆盖关系和安全约束。
