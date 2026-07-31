# 洗牌公平性审计契约 v1

## 1. 适用范围

本契约定义 Dedicated Server、Lobby、持久审计和争议调查工具之间的洗牌承诺与披露语义。
它不授权普通运营读取未结束牌局的完整牌序，也不允许任何管理入口修改权威结算。

## 2. 生命周期

1. Dedicated Server 使用 CSPRNG 产生 32 位洗牌种子和 256 位 `serverNonce`。
2. 发牌前计算 `seedCommitment`，本地 JSONL 只写 Commitment 事件。
3. 服务端使用 `UE-FRandomStream-FisherYates-v1` 完成洗牌和发牌。
4. 单局进入 Settlement 后，写 Reveal 事件并披露 `seedHex`、`serverNonceHex` 和 `deckOrderDigest`。
5. 每个 Reveal 与上一摘要链接为 `eventChainDigest`。
6. 整场结算把全部 `shuffleProofs` 和最终链摘要送往 Lobby。
7. Lobby 同步验证后，将完整报告原子写入 `match_results.payload`。

## 3. 承诺规范

规范文本不得插入空格或换行：

```text
fair-shuffle-v1|seed=<seedHex>|roomId=<roomId>|roundId=<roundId>|ruleId=<ruleId>|ruleVersion=<ruleVersion>|ruleHash=<ruleHash>|serverNonce=<serverNonceHex>
```

`seedCommitment = lowercase_hex(SHA256(UTF8(canonicalText)))`

字段约束：

- `seedHex`：8 位小写十六进制；
- `serverNonceHex`：64 位小写十六进制；
- `seedCommitment`、`deckOrderDigest`、`eventChainDigest`：64 位小写十六进制；
- `ruleHash`：当前 UE 规则快照使用 40 位小写十六进制；
- `roundId`：从 1 开始且在同一比赛内连续；
- `revealedAtUtc`：不得早于 `createdAtUtc`。

## 4. 事件链规范

首局的 `previous` 固定为 `genesis`，后续局使用上一局计算结果：

```text
fair-audit-chain-v1|previous=<previous>|roomId=<roomId>|roundId=<roundId>|commitment=<seedCommitment>|deckOrderDigest=<deckOrderDigest>|ruleHash=<ruleHash>
```

`eventChainDigest = lowercase_hex(SHA256(UTF8(canonicalText)))`

## 5. 数据保留与授权

- Commitment 文件属于服务器内部审计数据，权限不高于游戏服运行身份和调查服务身份。
- Reveal 只能在对应单局结算后产生。
- 最终证明随 `match_results.payload` 保留，保留期限按争议和合规策略执行。
- 未结束牌局不得通过 Admin、日志、指标、追踪或客服接口暴露 `seedHex`、`serverNonceHex` 或原始牌序。
- 导出、回放和争议调查必须经过 RBAC、二次确认、审批、TraceId 与工单审计。

## 6. 版本升级

改变随机源、Fisher–Yates 实现、随机流算法、牌实例编码、规范字段顺序或摘要算法时，必须发布新版本。
旧版本验证器必须保留到相关审计数据超过保留期限，禁止在原版本标识下静默改变行为。
