# 当前风险登记册与基线验证

> 盘点日期：2026-07-31。严重度按 Critical/High/Medium/Low；“阶段 0 处置”只冻结事实，不提前实施后续阶段。

## 1. 风险登记册

| ID | 严重度 | 风险 | 证据/影响 | 当前控制 | 阶段 0 处置 |
|---|---|---|---|---|---|
| R-001 | High | 基线工作树不干净 | `main` 上有 21 个跟踪修改和多个未跟踪公平性文件；无法仅靠 HEAD 复现 | Git diff 可追踪 | 记录 HEAD 与脏状态；进入阶段 1 前提交或隔离 |
| R-002 | High | Redis 访问撤销水位不可恢复 | `{prefix}:access-revoked-before:*` 无 PostgreSQL 来源；Redis 丢失可能暂时接受旧 access token | 短 access token、TTL、Auth 会话撤销 | 列为阶段 1+ 安全状态持久化/重建候选 |
| R-003 | High | Redis 幂等锁无 fencing token | 锁过期后旧持有者可与新持有者并发 | owner 校验释放、数据库唯一约束/CAS | 后续把业务唯一约束/Outbox 作为最终正确性边界 |
| R-004 | High | 房间写入、事件发布和分配不是同一事务 Outbox | 房间提交后事件/Allocator 失败会产生缺事件或孤儿实例 | 状态机、实例超时/回收、日志 | 后续按所有权逐步引入 Outbox/Inbox |
| R-005 | High | 结算所有权仍在 Lobby | `match_results` 和 result API 属于 Lobby；与最终独立 Settlement 目标有差距 | 实例 credential、复合主键、公平性校验 | 阶段 0 不拆分；后续先定义兼容契约和幂等边界 |
| R-006 | High | DS 进程崩溃会丢失局内权威状态 | 手牌/回合/座位主要在进程内；无状态恢复/热迁移 | 断线重连仅覆盖同进程；异常房间调查 | 明确可用性边界和争议处理，不在阶段 0重构 |
| R-007 | High | PostgreSQL 无正式版本化 downgrade | 只有幂等 `schema.sql`，没有迁移账本/版本/回滚脚本 | 独立 migration identity/Job、生产禁 DDL | 后续建立版本化迁移和备份恢复演练 |
| R-008 | High | 全链路关联三元组不统一 | 现有 W3C Trace、`X-Trace-Id` 和局部 `X-Request-Id`；无统一 correlation id | OTel、结构化日志、Admin TraceId | 后续先契约化再逐服务兼容接入 |
| R-009 | High | Windows Allocator 权限过大 | K8s 清单以 SYSTEM 运行并使用 hostPath | 节点隔离、内部服务认证 | 生产优先 Linux/Agones；Windows 方案需最小权限重做 |
| R-010 | Medium | Auth/Lobby K8s 安全上下文不完整 | 与 Admin/PlayerData 的 non-root、只读根、drop ALL 不一致 | 镜像运行时默认用户可能非 root，但清单未保证 | 后续统一 Pod/Container SecurityContext |
| R-011 | Medium | 观测 Compose 缺资源/健康/安全门禁 | 所有观测容器未统一配置 healthcheck、limits、non-root/read-only | 数据卷与 restart 策略 | 后续补齐并做容量压测 |
| R-012 | Medium | 观测端口与 Loki 无认证配置需网络隔离 | Grafana/Prometheus/Alertmanager/OTLP/查询网关发布；Loki `auth_enabled:false` | 预期本机/受信网络使用 | 生产由 Ingress、NetworkPolicy 和认证网关保护 |
| R-013 | Medium | Angular 缺 lint/test 自动化入口 | `package.json` 只有 build/typecheck | TypeScript 编译和生产构建 | 阶段 0 报告缺口；后续增加 ESLint 和组件/服务测试 |
| R-014 | Medium | 无 Helm Chart | 仓库只有原始 Kubernetes/Agones YAML | 可用 kubectl dry-run/脚本验证 | Helm 验证标记“不适用”，不能伪报通过 |
| R-015 | Medium | Lobby 公开房间列表未分页 | `GET /v1/rooms` 返回数组 | 公共房间量目前受业务规模约束 | 后续兼容增加分页，不改现有响应前先定版本策略 |
| R-016 | Medium | route GET 产生 Join Ticket | GET `/v1/rooms/{roomCode}/route` 每次签发新安全凭据 | 认证成员、短 TTL、响应不记录 | 必须设置 no-store；后续考虑兼容 POST |
| R-017 | Medium | Admin topology/reliability 状态为内存 | Admin 重启丢租约和最后成功快照 | 来源周期续租、重新采集 | 明确重启后暖机/stale 行为 |
| R-018 | Medium | 多服务管理命令可能部分成功 | 冻结/下线和强解散跨 Auth/Lobby/Allocator | Admin command Outbox、状态分类、审计 | 后续增加每步骤 Inbox/补偿与运行手册 |
| R-019 | Medium | Core 仍依赖 Unreal Engine 模块 | Core 无 UI/HTTP/Agones，但依赖 Engine，纯 C++ 测试困难 | Editor automation tests | 后续若需无引擎规则测试，再增量抽离；当前不移动 |
| R-020 | Low | Server 共享模块包含 Client RPC 声明 | `GuiyangMahjong` 同时编译进 Client/Server | 无 UMG/Slate；Client 实现位于独立 Client 模块 | 保留模块图自动化门禁 |
| R-021 | Medium | 公平性升级尚未形成干净发布基线 | 工作树有新 verifier、DS shuffle、契约和测试 | 已有单元/UE 测试源 | 由原任务责任人完成提交、构建、审计后再冻结 |
| R-022 | Medium | 配置能否使用最小权限生产账号无法仅靠仓库证明 | SQL 角色正确，但实际 Secret/连接串在外部 | Production 禁 runtime DDL；最小权限脚本 | 发布时强制执行 least-privilege smoke test |
| R-023 | High | Server 完整模块图仍包含 UMG/Slate | UBT JsonExport 显示 Server 单体可执行文件含 `UMG`、`Slate`、`SlateCore`；UE 5.8 Engine 为传递来源 | 项目 Server Build.cs 不直接依赖 UI、Client 模块隔离、`bUsesSlate=false` | 后续评估引擎裁剪/Target 选项并把真实目标加入模块图门禁 |

## 2. 缺失或不完整测试

| 范围 | 现状 | 缺口 |
|---|---|---|
| Angular | 生产 build/typecheck | 没有 lint、单元测试、组件测试脚本 |
| 真 UE Client → Auth/Lobby → DS | 分段 API 测试 + UE 多进程脚本 | 缺少一个从真实游客登录开始、经过 HTTP Lobby/Allocator、再进入真实 DS 的全链自动化 |
| DS 崩溃恢复 | Allocator 失败检测/房间 Failed | 没有牌局状态恢复测试，因为当前能力不存在 |
| 数据库回滚 | 外部 PostgreSQL 升级/事务测试 | 没有 downgrade 和从备份恢复演练 |
| Redis 灾难恢复 | 缓存回源、事件回源测试 | 没有撤销水位丢失的安全测试与恢复方案 |
| 网络故障注入 | 有部分超时/重试单测 | 缺少跨服务延迟、分区、重复投递的系统级混沌测试 |
| Kubernetes | 静态清单/Agones 脚本 | 缺少在真实集群上的 rollout、PDB、NetworkPolicy、优雅终止验证 |
| 可观测 | 规则契约和仪表盘 | 缺少告警从触发到通知接收器的端到端演练 |

## 3. 验证结果

本节在阶段 0 文档生成后由实际命令结果更新。任何缺失工具、外部依赖或正在占用构建资源的进程都会标记为“未验证/受阻”，不会视为通过。

| 验证 | 命令 | 结果 | 备注 |
|---|---|---|---|
| .NET restore | `dotnet restore Services/GuiyangMahjong.Services.slnx` | 通过 | 全部项目还原成功 |
| .NET build | `dotnet build ... -c Release --no-restore` | 通过 | 0 warning、0 error |
| .NET test | `dotnet test ... -c Release --no-build` | 通过 | 常规基线 157 通过；另用临时 PostgreSQL 17/Redis 8 容器执行 16 个外部持久化测试，合计 173 通过、0 失败；容器已删除 |
| Angular install | `npm ci` | 通过但有告警 | 安装 391 个包；审计报告 3 个 moderate 漏洞，并提示 4 个包的安装脚本待批准 |
| Angular typecheck | `npm run typecheck` | 通过 | TypeScript 编译检查通过 |
| Angular build | `npm run build` | 通过 | 生产构建通过，初始包体 146.21 kB（raw） |
| Angular lint | `npm run lint` | 不可执行 | 当前没有脚本 |
| Angular test | `npm run test` | 不可执行 | 当前没有脚本 |
| Linux Compose | `docker compose -f Deploy/linux/compose.yaml config --quiet` | 通过 | 设置 `COMPOSE_DISABLE_ENV_FILE=1`，仅使用安全占位变量，未读取 `.env` |
| Observability Compose | `docker compose -f Deploy/observability/compose.yaml config --quiet` | 通过 | 使用安全占位变量 |
| Kubernetes/Agones | YAML 解析、结构检查及 `kubectl create --dry-run=client` | 部分通过 | PyYAML 成功解析并检查 32 个文档；因无当前集群，kubectl 无法完成 API/CRD schema discovery，未修改集群 |
| Helm | `helm template` | 不适用 | 仓库无 Chart |
| Unreal module graph | UBT `JsonExport -NoMutex` 等价检查 | 部分通过 | Client/Server 项目模块边界符合预期；Server 完整单体依赖图仍传递包含 `UMG`、`Slate`、`SlateCore`。标准脚本因既有 UBT mutex 被占用未能直接运行 |
| Unreal automation | UnrealEditor-Cmd `GuiyangMahjong.*` | 受阻/未验证 | 现有 Editor 二进制早于当前源码，且阶段开始前的 Server UBT 构建仍占用构建资源；未以陈旧二进制伪报通过 |
| Client/Server build | UBT Client/Server | 受阻/未验证 | 阶段开始前已有 Server 构建进程持续运行且未产出本次可确认结果；未终止非本阶段启动的进程，Client 当前源码构建未执行 |
| 静态契约门禁 | 观测、治理、调查历史、容量、SLO、Schema 隔离脚本 | 通过 | 监控容量脚本的 Angular 旧路径已按当前拆分结构修复后通过 |
| 目录治理门禁 | `Scripts/Test-ProjectStructureGovernance.ps1` | 失败 | 阶段开始前已有未登记核心文档 `Docs/SOLUTION_PROJECT_ARCHITECTURE_AND_DIRECTORY_STRUCTURE_20260731.md`；阶段 0 不删除或移动该文件 |

## 4. 进入阶段 1 的门槛

必须同时满足：

1. 阶段 0 的 `.NET`、Angular build/typecheck、Compose 和 Unreal 模块图基线有明确结果；
2. 当前脏工作树被提交、拆分或由负责人明确接受为阶段 1 起点；
3. 公平性相关未提交代码至少完成 Client/Server 构建和对应单元/UE 测试；
4. 阶段 1 的 API/数据库兼容范围能引用本盘点文档；
5. 不把缺少 Angular test/lint、Helm Chart 或外部集群验证伪装成已通过。
6. 对 Server 中 UMG/Slate 的传递依赖形成明确的可接受性决定或可执行裁剪方案。

在上述门槛未满足前，结论应为“有条件进入”或“不满足”，而不是无条件通过。
