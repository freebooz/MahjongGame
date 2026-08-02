# 阶段8.6 PlayerData退役报告

## 实际状态与目标

阶段8.2至8.5已经分别把Replay、资产与奖励、聊天授权、举报与支付证据迁移到GameData、Economy、Community和Admin/TrustSafety。PlayerData没有剩余权威职责，本阶段将其从解决方案、网关、部署、镜像矩阵和管理监控来源中移除。

## 兼容与数据策略

`/api/v1/player-data/**` 在阶段0清单中只是没有真实公开端点的预留路由，因此删除后不会移除有效玩家API。PlayerData内部兼容URL随部署停止，不再接受新调用。旧`player_data` Schema在核对完成后撤销运行身份并冻结为只读历史证据，不立即物理删除；这避免破坏审计保留与紧急回滚。保留期结束后的物理删除不属于本阶段，必须单独审批。

数据库变更不新增业务表、不改变新所有者数据。Redis和消息事件无变化。Angular仅移除已经退役的健康来源提示；Unreal和麻将规则无变化。

## 发布与回滚

发布前必须证明旧入口调用量为零，并执行`sql/stage-8.6-player-data-decommission-validation.sql`取得零行结果。随后停止旧实例、冻结数据库身份、部署新编排并观察目标服务。回滚需要恢复上一版本编排和镜像、通过密钥系统重新注入凭据，再执行回滚SQL；不得在仓库或日志记录密码。

## 实际修改与验证结果

- PlayerData生产与测试项目已从解决方案移除，架构测试也不再通过项目引用重新构建它；源目录暂留一个发布周期作为可审计回滚基线，不进入任何运行或镜像入口。
- 删除Kubernetes工作负载、Compose服务、EdgeGateway路由、CI镜像矩阵和Linux镜像构建项；新环境不再创建PlayerData业务表或执行阶段8.2/8.3/8.5历史迁移。
- Admin删除PlayerData健康来源，Community删除旧适配器凭据，GameData删除旧Replay HTTP摄取入口；Admin资产命令明确指向Economy。
- 数据库运行身份设置为`NOLOGIN`，撤销旧Schema写权限；历史Schema仅向监控身份开放只读访问。
- `dotnet build Services/GuiyangMahjong.Services.slnx -m:1`：通过，0个警告、0个错误；
- `dotnet test Services/GuiyangMahjong.Services.slnx --no-build --no-restore -m:1`：288项通过、26项需要外部依赖的既有测试显式跳过、0项失败；
- Angular：`npm ci`、lint/TypeScript检查、3项安全与降级测试、生产构建全部通过；依赖审计报告3个中等级既有风险，未执行可能引入破坏性升级的`audit fix --force`；
- Compose配置校验、全部Kubernetes和数据库YAML离线解析：通过；
- 临时PostgreSQL 17完成门禁零结果、冻结、权限断言和回滚演练：通过；验证容器已精确删除。

Unreal、Redis、NATS事件、API响应模型、数据库新所有者和麻将规则没有变化，因此本阶段不执行UE构建或新增消息迁移。

## 验收结论与剩余事项

阶段8.6代码与部署基线验收通过，PlayerData已退出在役运行路径。真实生产切换仍必须由变更工单证明旧入口调用量为零，并在目标数据库执行门禁；本地演练不能替代生产数据核对。历史Schema的物理删除、源目录最终删除和角色物理删除须等保留期届满后另行审批，不应与本次可回滚退役合并。
