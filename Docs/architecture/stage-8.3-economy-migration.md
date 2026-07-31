# 阶段 8.3 奖励与资产迁移报告

## 完成边界

- 新增 `GuiyangMahjong.Economy`，由 Rewards、Inventory 与 Infrastructure 组成一个模块化部署单元。
- `inventory.wallet_balances`、`inventory.wallet_transactions`、`reward.reward_grants` 成为权威表；`economy_integration.platform_outbox` 与业务事务同库提交。
- PlayerData 保留奖励、钱包和余额三个旧 URL，但只调用 `ILegacyEconomyClient`；代码和数据库触发器共同阻止旧表继续写入。
- Admin 钱包命令直接调用 Economy，不再通过 PlayerData；Angular、Lobby、DS 均未获得资产表写权限。
- Redis 无变化；聊天授权、举报/支付证据和 Backoffice 读模型留待 8.4、8.5，PlayerData 停服留待 8.6。

## 数据迁移与兼容

源表依次映射到 `inventory.wallet_balances`、`reward.reward_grants` 和
`inventory.wallet_transactions`。旧奖励没有保存 EventId，因此迁移脚本使用
`md5('player-data-reward:' + reward_grant_id)::uuid` 生成稳定历史标识，并用
`legacy:` 前缀标记来源；新奖励同时以 EventId、RewardGrantId 和 SourceReference 去重。

执行顺序：预建 Economy Schema → 停止旧写流量 → 执行
`Migrations/Stage8_3_MigrateFromPlayerData.sql` → 执行 `Stage8_3_Validate.sql` →
部署 Economy → 部署 PlayerData 兼容适配器 → 将 Admin 切至 Economy。迁移失败会在同一事务回滚。

紧急回滚时先停止 Economy 新写流量，再执行 `Stage8_3_Rollback.sql` 恢复旧表写能力。不得删除
Economy 记录，必须先核对切换后交易并通过关联工单决定反向迁移。

## 配置和权限

新增 `Economy__PostgresConnectionString`、三类用途隔离 Token、端口和资源限制，均支持环境变量覆盖。
生产运行配置强制关闭 DDL。`mahjong_economy_rw` 仅访问 Economy 自有 Schema；PlayerData 与 Admin
均无这些表的数据库写权限。Kubernetes Secret 文件只保留占位符，不包含实际凭据。

## 验收结果（2026-07-31）

- Release 全解决方案构建通过：0 警告、0 错误。
- 全解决方案测试通过；新增 Economy HTTP 测试覆盖认证、奖励、撤销、重复命令和双人审批。
- PostgreSQL 17 临时实例完成带样本数据的迁移、逐项核对和旧表写拒绝；临时容器已删除。
- Compose 展开校验通过；Kubernetes 全部 YAML 离线语法通过。由于本机无当前集群，`kubectl`
  API 发现校验未完成，不能据此宣称生产集群部署成功。
- 本小步没有执行 Angular 或 Unreal 构建，因为未修改其源码、Target 或 Build.cs。

阶段 8.3 代码基线达到单写者技术验收条件。生产切换仍须在授权停写窗口记录源表数量、锁等待、
校验输出、审批和回滚负责人。下一次只可独立实施 8.4 聊天授权迁移。
