# 跨服务契约

`Contracts` 保存可被生产者、消费者和 CI 独立读取的机器契约，不放置任一服务的内部
领域模型或实现源码。

## 目录

| 目录 | 职责 |
| --- | --- |
| `Authentication` | Auth 生产、Lobby 等消费者使用的身份与令牌线格式 |
| `Monitoring` | Dedicated Server、Lobby、Admin 之间的运行遥测 Schema，以及洗牌公平性审计规范 |
| `OpenAPI` | HTTP API 的版本化 OpenAPI 描述 |

## 变更规则

- 已发布契约不得原地改变既有字段含义、单位、大小写、签名算法或必填性；
- 不兼容变更创建新主版本文件，并保留旧版本直到所有生产者和消费者完成迁移；
- 固定测试向量中的密钥只能用于自动化，禁止进入任何部署 Secret；
- 生产者和消费者必须在各自测试项目中独立验证同一契约，不得通过引用对方生产程序集
  共享实现；
- 契约变更必须触发服务构建、契约测试与相关 Docker 镜像构建矩阵。

当前安全审计契约：

- `Monitoring/shuffle-fairness-v1.md`：定义开局承诺、局后披露、牌墙摘要和审计事件链；
- `OpenAPI/lobby-v1.openapi.yaml`：定义最终结算中的 `shuffleProofs` 与 `eventChainDigest` 线格式。
