# 阶段 8.4 Community/Chat 授权迁移报告

## 实际边界

迁移前，PlayerData 的 `/internal/chat/messages/authorize` 同步查询 Identity 的玩家监控接口，
再根据 `mutedUntilUtc` 判断是否允许发送。PlayerData 没有聊天正文、聊天授权表、Redis 键或迁移数据。
禁言和解除禁言仍由 Identity/Administration 权威维护，本阶段不改变处罚写模型。

迁移后，`GuiyangMahjong.Community` 成为聊天发送授权边界：

- `POST /internal/chat/messages/authorize` 保持原请求、响应及 200/423 语义；
- 只接收 MessageId、PlayerId、RoomId 和请求时间，不接收或记录聊天正文；
- 使用专用只读工作负载身份查询 Identity；账号不存在、依赖错误和超时均失败关闭；
- PlayerData 旧 URL 只通过 `ILegacyCommunityChatClient` 转发，不再查询 Identity 或计算禁言策略；
- 直接聊天网关和 PlayerData 兼容适配使用不同入站凭据。

## API、数据和兼容策略

新增 Community 内部 API，旧 PlayerData API 未删除、请求和响应结构未改变。数据库、Redis、消息事件均无变化，
因此没有数据迁移 SQL。Admin ChatArchive 是聊天合规查询接口，不属于发送授权，本阶段未修改。

推荐切换顺序：部署 Community → 验证 Identity 只读调用 → 部署 PlayerData 适配器 → 观察旧入口调用量 →
聊天网关改为直连 Community。兼容期内不得同时在 PlayerData 和 Community 实现两套策略。

回滚不涉及数据：先把聊天网关切回 PlayerData 旧入口，再回滚 PlayerData/Community 镜像。由于授权失败默认拒绝，
回滚期间的依赖故障不会误放行已禁言玩家。

## 配置和部署

Community 新增直接聊天网关、PlayerData 适配器、Identity 只读三类隔离凭据，以及 Auth 地址和超时配置。
配置均支持环境变量覆盖，不包含实际 Token。Compose 和 Kubernetes 服务只暴露集群内端口；Compose 的宿主机端口
绑定到 `127.0.0.1`，仅用于本地诊断。镜像使用非 root、只读文件系统、资源限制和健康探针。

## 本阶段不处理

- 举报和支付证据迁移、Backoffice 专用读模型（阶段 8.5）；
- PlayerData 停服和旧接口删除（阶段 8.6）；
- 聊天正文归档、内容审核模型和复杂风控；
- Identity 禁言写模型和 Admin 审批流程。

## 验收记录

2026-07-31 技术验证结果：

- `dotnet restore`、Release 全解决方案构建通过，0 警告、0 错误；
- 全解决方案 295 项测试通过，28 项需要外部 PostgreSQL、Redis 或 NATS 的既有测试跳过，0 项失败；
- Community 测试覆盖身份认证、输入校验、允许、禁言、Identity 故障失败关闭和响应兼容；
- 架构测试证明 PlayerData 不再包含 Identity 聊天查询或策略实现；
- Compose 使用示例环境展开通过，Kubernetes 全部 YAML 离线语法通过；本机没有可用集群上下文，未宣称集群应用成功；
- Community Docker 镜像完整构建通过，验证镜像随后已删除；
- 本阶段未修改数据库、Redis、Angular 或 Unreal 源码，因此没有迁移、前端构建或 UE 构建。

生产切流仍需记录旧入口调用量、Identity 依赖错误率、授权 P95/P99、423 比例、审批人和回滚负责人。
