# 贵阳捉鸡麻将独立大厅服务

> 阶段 4 已将单部署单元内部划分为 Lobby、Rooms、Matchmaking、Reconnection、
> GameRouting、Administration 和 Infrastructure。详细设计、迁移与验收结果见
> `Docs/architecture/stage-4-lobby-control-app.md`。

该应用是 UE 客户端与一桌一服 GameServer 之间的控制面，不参与洗牌、手牌、出牌裁决或计分。

## 已实现范围

- `GET /v1/lobby/bootstrap`：公告、在线状态、协议版本。
- `GET /v1/rooms`：公开房间目录。
- `POST /v1/rooms`：原子创建六位房间号。
- `POST /v1/rooms/{roomCode}/join`：密码校验、限流和加入房间。
- `GET /v1/rooms/{roomCode}/route`：查询已分配 GameServer 路由。
- `POST /v1/reconnect/route`：按房间和牌局标识恢复路由。
- `GET /v1/events`：WebSocket 大厅事件。
- `GET /openapi/v1.yaml`：由独立应用提供版本化 OpenAPI 契约。

阶段 3 才实现 GameServer 注册、心跳和分配；阶段 2 未分配服务器时，路由接口明确返回
`SERVER_UNAVAILABLE`，不会伪造地址或票据。

## 存储模式

### InMemory

仅供本地开发和自动化测试。进程重启后数据清空，不允许用于生产环境。

```text
Lobby__Persistence__Mode=InMemory
```

### RedisPostgres

PostgreSQL 使用 `room_code` 唯一约束保证跨实例房间号原子性，并保存完整房间快照；Redis 保存
24 小时热快照。缓存未命中时从 PostgreSQL 恢复并回填 Redis。

```text
Lobby__Persistence__Mode=RedisPostgres
Lobby__Persistence__RedisConnectionString=127.0.0.1:6379,abortConnect=false
Lobby__Persistence__PostgresConnectionString=Host=127.0.0.1;Port=5432;Database=guiyang_lobby;Username=...;Password=...
Lobby__Persistence__RedisKeyPrefix=guiyang:lobby:v1
```

数据库表由启动托管服务读取 `Storage/schema.sql` 幂等初始化。生产上线前必须在真实 Redis 和
PostgreSQL 上执行断电、重启、并发唯一性和故障恢复测试。

## 身份与密钥

- `Lobby__TokenSigningKey` 必须至少 32 个字符，并应由密钥服务或安全环境变量注入。
- 生产环境拒绝以 `development-only` 开头的签名密钥。
- 服务没有公开 Token 签发端点；玩家 Token 必须由受信 Auth 服务签名。
- 房间密码仅保存 PBKDF2-HMAC-SHA256 盐化摘要，日志只记录 RequestId、RoomId、PlayerId 和结果。

## 本地运行

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Lobby__TokenSigningKey = '<至少 32 字符的本地开发密钥>'
dotnet run --project Services/GuiyangMahjong.Lobby/GuiyangMahjong.Lobby.csproj --urls http://127.0.0.1:18080
```

## 构建与测试

```powershell
dotnet build Services/GuiyangMahjong.Lobby.Tests/GuiyangMahjong.Lobby.Tests.csproj -c Release
dotnet test Services/GuiyangMahjong.Lobby.Tests/GuiyangMahjong.Lobby.Tests.csproj -c Release --no-build
```
