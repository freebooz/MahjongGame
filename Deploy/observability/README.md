# 贵阳麻将可观测性开发栈

本目录提供工作流 D 的本地/预生产基线：OpenTelemetry Collector、Loki、Tempo、Prometheus、Alertmanager 和 Grafana。Loki/Tempo 不直接绑定宿主机公网接口；Admin 只能通过带独立只读令牌的 Nginx 查询网关访问 Loki。

## 启动

1. 将 `.env.example` 复制为 `.env`，把两个示例值替换为至少 32 字符的随机密钥。
2. 执行：

```powershell
docker compose -f Deploy/observability/compose.yaml --env-file Deploy/observability/.env up -d
```

3. 为五个 .NET 服务设置 `Observability:Enabled=true`，并保持 `OtlpEndpoint=http://127.0.0.1:4317`。
4. Admin 的 `Admin:CentralLogs` 设置为：

```json
{
  "Enabled": true,
  "BaseUrl": "http://127.0.0.1:13100",
  "QueryToken": "与 LOKI_QUERY_TOKEN 相同的只读令牌",
  "TimeoutSeconds": 5,
  "LookbackHours": 24,
  "MaxEntries": 1000
}
```

5. 浏览 `http://127.0.0.1:13000`。Prometheus 与 Alertmanager 分别只绑定本机 `19090`、`19093`。

如果忘记本机 Grafana 初始密码，可在不读取旧密码的情况下重置：

```powershell
docker exec guiyang-mahjong-observability-grafana-1 grafana cli admin reset-admin-password <新的强密码>
```

## 安全与容量边界

- 生产环境不得使用 `.env` 文件，应由密钥管理系统注入 Grafana 密码和 Loki 查询令牌。
- Loki 保留 7 天、单次查询最多 5000 行；Prometheus 保留 15 天。生产环境应改用对象存储和多副本模式。
- 指标标签禁止 RoomId、PlayerId、MatchId 和 ServerInstanceId。逐房间定位使用 Grafana 日志面板及 TraceId，避免 Prometheus 高基数。
- Admin 日志导出仍要求角色、案件审批、工单和审计；前端不会拿到 Loki 地址或凭据。
- Collector 在应用脱敏之后再删除敏感属性并清理 Bearer/卡号模式，形成双层防线。

## 配置验证

```powershell
./Scripts/Test-ObservabilityContracts.ps1
```

该命令验证日志字段、四个仪表盘 JSON、告警清单、Collector 管线和 Compose 语法，不启动容器。
