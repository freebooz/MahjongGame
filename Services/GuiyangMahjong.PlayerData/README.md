# GuiyangMahjong.PlayerData

PlayerData 是阶段 8 拆解期间的短期兼容适配层，不得新增业务能力。

当前所有权：

- 玩家资料和会话属于 Identity；
- 战绩、结算和回放证据属于 GameData；
- 奖励、资产余额和交易流水属于 Economy；
- 聊天发送授权属于 Community/Chat。

旧的奖励、钱包、余额、回放和聊天 URL 暂时保留响应兼容，但只调用对应新所有者，禁止继续写旧表或在
PlayerData 内复制策略。支付与举报证据将在阶段 8.5 迁移；全部兼容流量归零并完成数据核对后，阶段 8.6
才能停止部署 PlayerData。

保留的内部兼容入口：

- `POST /internal/sources/reward-claims` → Economy；
- `POST /internal/sources/replays` → GameData；
- `POST /internal/admin/wallet-operations` → Economy；
- `POST /internal/chat/messages/authorize` → Community；
- `GET /internal/monitoring/players/{playerId}/balances` → Economy。

尚未迁移的入口：

- `POST /internal/sources/payment-orders`；
- `POST /internal/sources/reports`。

所有兼容 POST 均不执行透明重试。生产环境必须使用用途隔离的工作负载凭据，并保持数据库 DDL 关闭。
