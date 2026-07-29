# 持久历史与调查闭环契约 v1

## 房间事件

- `room_event_history.event_id` 是全局幂等键；重复投递必须返回成功语义但不得新增记录。
- PostgreSQL 是合规保留期内的历史权威，Redis 仅保存最近七天、最多 500 条热数据。
- Redis 为空或不完整时，Lobby 必须回源 PostgreSQL 并恢复热缓存。
- 普通运行身份不得更新、删除或截断房间事件；保留期清理必须使用独立迁移/保留身份。
- 每条事件保留 RoomId、MatchId、StateSequence、TraceId、发生时间和原始 JSON 载荷。

## 玩家历史

- `player_room_history` 由房间快照事务投影，记录进入、离开和离开原因。
- `player_connection_history` 由不可变 `PlayerConnectionChanged` 事件投影，EventId 同时作为幂等键。
- Admin 通过 Lobby 只读接口使用键集分页查询，单页上限 200。
- 查询要求调查角色和工单，且每一页读取均写入操作者、工单和 TraceId 审计。

## 回放

- 回放目录通过已有 PlayerEvidence 投影关联 PlayerId、MatchId、ObjectKey 和内容 SHA-256。
- 浏览器不得取得对象存储地址或读取令牌，只能取得最长十分钟且默认五分钟的 Admin 签名 URL。
- 签名必须绑定 CaseId、PlayerId、EventId、OperatorId 和过期时间，不能跨玩家、案件或操作者复用。
- Admin 代理下载时限制对象大小并验证内容 SHA-256；失败时不得返回未验证内容。

## 证据包与案件关闭

- 证据包范围完全继承案件 TargetType、TargetId、TicketId，客户端不能提交额外玩家范围。
- 包含案件快照、操作、审计、资产流水、玩家证据、持久历史或房间事件。
- `CanonicalPayloadHash` 为证据正文 UTF-8 JSON 的 SHA-256。
- 案件只能从 `Open` 单向转为 `Closed`；请求人不得关闭自己的案件。
- 关闭必须写入结论、关闭人、关闭时间及最终证据包哈希。

## 聊天合规查询

- Admin 只连接独立聊天归档只读网关，不持有聊天库写入或管理员权限。
- 每次正文查询必须存在未过期的独立审批 grant。
- 玩家、时间窗口和 scopes 必须是 grant 的子集，超出范围默认拒绝。
- 返回结果带操作者、工单、TraceId 和查看时间水印；审计账本不记录聊天正文。
