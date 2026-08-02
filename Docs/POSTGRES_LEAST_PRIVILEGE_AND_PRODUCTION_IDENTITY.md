# PostgreSQL 最小权限与生产身份运行手册

## 1. 目标与边界

本方案将“发布期结构变更”“业务运行读写”“只读监控”“审计追加”和“审计归档派发”拆成独立数据库身份。生产服务不得执行 DDL，不得复用 `mahjong` 共享账号；Admin 生产入口只接受企业 OIDC/JWT、MFA 和最长十分钟的短会话。

角色定义位于 `Deploy/postgres/least-privilege/`，顺序固定为：

1. `001_roles.sql`：幂等创建权限角色和登录身份，不包含密码。
2. 四个服务的 `Storage/schema.sql`：由发布身份执行兼容迁移。
3. `002_grants.sql`：转移对象所有权、撤销 `PUBLIC`、授予逐表权限。
4. `004_set_login_passwords.sql`：从密钥系统注入密码。
5. 观测窗口结束后才可人工执行 `003_disable_legacy_role.sql`。

不得把 `003_disable_legacy_role.sql` 放入首次滚动发布。它会检查旧账号活动连接，存在连接时主动失败。

## 2. 角色矩阵

| 登录身份 | 激活权限角色 | 允许 | 明确禁止 |
|---|---|---|---|
| `mahjong_migration` | 对象所有者 | 版本化 DDL、所有权、授权 | 注入应用 Pod、处理业务流量 |
| `mahjong_auth` | `mahjong_auth_rw` | Auth 六张表读写 | Lobby、PlayerData、Admin、DDL |
| `mahjong_lobby` | `mahjong_lobby_rw` | Lobby 三张表读写 | Auth、PlayerData、Admin、DDL |
| `mahjong_player_data` | `mahjong_player_data_rw` | `player_data` 表读写 | 其他域、DDL |
| `mahjong_admin` | `mahjong_admin_rw` | 管理工作流表读写、审计账本 SELECT/INSERT | 审计 UPDATE/DELETE/TRUNCATE、归档派发、DDL |
| `mahjong_monitor` | `mahjong_monitor_ro` | 已授权表 SELECT | 写入、DDL、函数执行 |
| `mahjong_audit_writer` | `mahjong_audit_append` | 审计账本 SELECT/INSERT | UPDATE/DELETE/TRUNCATE、其他管理表 |
| `mahjong_archive` | `mahjong_archive_dispatch` | 归档 Outbox SELECT/UPDATE | 读取原始审计账本、业务表、DDL |

登录身份使用 `NOINHERIT`。连接串通过 `Options=-c role=<唯一权限角色>` 显式激活角色，避免后续误授予的成员权限自动叠加。

## 3. 发布顺序与回滚

推荐滚动顺序：

1. 备份并验证恢复点。
2. 运行迁移 Job；迁移必须向后兼容旧版本服务。
3. 创建/轮换各工作负载密码，更新 External Secrets 或 Vault 引用。
4. 先发布 Auth、Lobby、PlayerData，再发布 Admin；所有服务配置 `ApplyDatabaseMigrations=false`。
5. 检查 readiness、权限拒绝指标和 PostgreSQL `pg_stat_activity`。
6. 保持旧账号可登录一个回滚窗口；回滚只切回旧应用版本和旧凭据，不回滚兼容结构。
7. 确认没有旧连接后执行 `003_disable_legacy_role.sql`。

若新版本失败，恢复应用 Secret 引用即可。不得为了快速恢复而把运行身份提升为对象所有者或超级用户。

## 4. 密钥轮换

- 密码只存在于 Vault、云密钥管理或 Kubernetes External Secrets；仓库和日志不得出现真实值。
- 轮换采用“新密码写入数据库 → 更新 Secret → 滚动 Pod → 检查旧连接 → 撤销旧值”。
- 数据库密码建议不超过 30 天；企业 Admin 会话与角色撤销 SLA 均为 10 分钟。
- migration Secret 与应用 Secret 分开，Job 完成后删除短期凭据和 Pod。
- Admin 审计归档使用独立连接，不能回退到管理连接；生产启动门禁会拒绝相同连接字符串。

## 5. Admin 生产身份与浏览器安全

- JWT 校验由 ASP.NET Core JwtBearer 完成，要求 HTTPS Authority、受众、签名、有效期；中间件额外要求合法 `sub`、已知角色、`amr=mfa`、`iat` 和 `jti`。
- `iat` 超过十分钟即拒绝，确保离职和角色变更在 SLA 内收敛。
- 生产配置禁止本地 `Principals` 和共享 `ReadOnlyAccessToken`。
- Angular 管理令牌只驻留当前页面内存，不写入 `localStorage`、`sessionStorage` 或日志。
- 响应启用 HSTS、严格 CSP、`nosniff`、禁止 framing、权限策略和 `no-store`。
- 普通读取、搜索、敏感证据和导出使用独立限流桶；前端隐藏按钮不构成授权，API 每条操作仍执行 RBAC、二次确认和异人审批。

## 6. 验证与应急

CI 和本地隔离环境运行：

```bash
LEAST_PRIVILEGE_ADMIN_URL='postgresql://postgres:***@127.0.0.1:5432/testdb' \
PGHOST=127.0.0.1 PGPORT=5432 \
./Scripts/Linux/test-postgres-least-privilege.sh
```

脚本会实际连接 Auth、审计追加和归档身份，确认允许查询成功，并确认跨域查询、DDL、审计 UPDATE 和归档读取原始账本均失败。

权限异常时先冻结发布和高风险命令，导出 `pg_auth_members`、表 ACL、`pg_stat_activity` 和相关 TraceId，禁止临时授予超级用户。确认原因后通过新的版本化授权迁移修复，并关联审批工单。
