# 阶段 8.5 Backoffice 读模型迁移报告

## 实际边界

Admin 已拥有 `admin_monitor.player_evidence` 专用调查读模型、受控摄取 API、类型级 RBAC、敏感字段拒绝、
读取审计和幂等约束。迁移前 PlayerData 仍先保存 Report、PaymentOrder，再由本地 Dispatcher 投影到 Admin，
形成不必要的中转和旧表写路径。

本阶段复用现有模块化 Admin/TrustSafety 能力，不新建空壳微服务：

- PlayerData 的举报和支付旧 URL 保持请求、响应、Bearer 与 `Idempotency-Key=eventId` 兼容；
- 旧 URL 只通过 `ILegacyAdminEvidenceClient` 直接调用 Admin 摄取 API；
- PlayerData 不再调用 `RecordEvidenceAsync`，也不为新请求创建 `projection_outbox`；
- 数据库触发器拒绝 Report、PaymentOrder 写回旧表；
- Admin 查询继续执行证据类型 RBAC、工单约束、脱敏和不可跳过的读取审计；
- 生产 `ProjectionEnabled=false`，历史积压必须在切换前清零。

Admin 写入的是自身专用调查读模型，不直接修改 Identity、Room、Settlement、Inventory 或其他服务业务表。
普通运营人员只能按角色和工单查看脱敏投影，不能修改对局结果或资产权威记录。

## 数据迁移

迁移脚本：

- `sql/stage-8.5-backoffice-evidence-migration.sql`：将非 Replay 历史证据幂等复制到 Admin，并在同一事务逐字段核对；
- `sql/stage-8.5-backoffice-evidence-validation.sql`：检查缺失/不一致投影和未完成旧 Outbox；
- `sql/stage-8.5-backoffice-evidence-rollback.sql`：只移除旧写门禁，不删除 Admin 审计证据。

执行顺序：停止旧写流量 → 等待旧 Dispatcher 清空 → 迁移与核对 → 部署 PlayerData 适配器 →
保持 Dispatcher 关闭 → 观察旧入口与 Admin 摄取指标。唯一键冲突或字段不一致会令迁移事务失败，禁止覆盖历史证据。

## API、配置、Redis和事件

外部兼容 API 无结构变化。PlayerData 继续使用既有 Admin 摄取地址和专用凭据，未新增数据库连接共享。
Redis 无变化；没有新增业务事件；Angular 页面和查询 API 无变化。旧 Dispatcher 代码仅为紧急回滚保留，
阶段 8.6 删除 PlayerData 时一并移除。

## 回滚

先停止新直达流量，再恢复旧 PlayerData 镜像并执行回滚 SQL。Admin 已有记录不可删除；恢复 Dispatcher 前必须
利用 EventId 核对重复投影。回滚不得同时开放新旧两个写入口，也不得让 Admin 获得其他业务 Schema 写权限。

## 本阶段不处理

- PlayerData 停止部署、旧接口删除和旧 Schema 归档（阶段 8.6）；
- 新的举报处置工作流、支付系统或复杂风险模型；
- Angular、Unreal、Redis 和麻将规则。

## 验证结果

- `dotnet build Services/GuiyangMahjong.Services.slnx -m:1`：通过，0 个警告、0 个错误；
- `dotnet test Services/GuiyangMahjong.Services.slnx --no-build --no-restore -m:1`：297 项通过、28 项显式跳过、0 项失败；跳过项均为需要外部 PostgreSQL、Redis 或 NATS 的既有测试；
- 首次并行执行全解决方案测试时，Schema 发布目录被并行项目重写且两个测试程序集尚未生成；重新完整构建并按单节点顺序执行后全部通过，确认属于共享构建产物竞争而非业务回归；
- 使用临时 PostgreSQL 17 容器执行迁移、重复执行、校验和写门禁验证：通过；历史 Report、PaymentOrder 成功迁入 Admin，重复迁移无副作用，新旧表字段核对一致，旧写入被拒绝；临时容器已精确删除；
- `docker compose --env-file Deploy/linux/.env.example -f Deploy/linux/compose.yaml config --quiet`：通过；
- Kubernetes 与数据库部署 YAML 使用离线解析校验：通过；
- PlayerData Dockerfile 以仓库根为上下文完成 Release 镜像构建：通过；验证镜像已精确删除；
- Angular、Unreal、Redis 和外部 API 响应结构未发生变化，因此本阶段不重复执行其构建或迁移。

阶段 8.5 验收通过。PlayerData 已不再承接新的举报和支付订单证据写入；阶段 8.6 可在确认兼容调用量归零、旧 Outbox 清空并完成停服窗口审批后，下线 PlayerData 部署和剩余旧代码。
