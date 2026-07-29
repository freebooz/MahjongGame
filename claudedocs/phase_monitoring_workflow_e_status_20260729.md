# 工作流 E：数据库最小权限与生产身份执行报告

执行日期：2026-07-29  
结论：代码、部署契约、真实数据库拒绝测试和文档已完成；当前运行中的旧共享数据库未被原地破坏性切换。

## 完成项

| 工作项 | 状态 | 证据 |
|---|---|---|
| MON-040 PostgreSQL 角色矩阵 | 完成 | `Deploy/postgres/least-privilege/001_roles.sql`、`002_grants.sql` |
| 运行身份禁止 DDL | 完成 | 四服务 `ApplyDatabaseMigrations` 生产启动门禁 |
| 审计只追加 | 完成 | 表 ACL + 既有 mutation trigger + 实际 UPDATE 拒绝测试 |
| 归档派发隔离 | 完成 | `mahjong_archive_dispatch` 与 Admin 独立连接 |
| MON-041 Compose/K8s 身份拆分 | 完成 | Compose migration service、K8s Secret 示例与 migration Job |
| 密钥轮换和旧账号停用 | 完成 | `004_set_login_passwords.sql`、`003_disable_legacy_role.sql`、运行手册 |
| CI 真实 PostgreSQL 越权门禁 | 完成 | `Scripts/Linux/test-postgres-least-privilege.sh` 与 services-ci |
| MON-042 OIDC/MFA/短会话 | 完成 | JwtBearer、MFA/角色映射、iat/jti、10 分钟撤销 SLA |
| 生产回退令牌禁用 | 完成 | 生产启动校验要求空本地 Principal/ReadOnly token |
| WORM 前置条件 | 已有并强化 | 高风险执行仍要求 HTTPS 不可变归档，归档数据库身份独立 |
| MON-043 HTTPS/HSTS/CSP/安全头 | 完成 | Admin 管道统一覆盖 Angular 静态入口与 API |
| 分级限流 | 完成 | read/search/evidence/export 独立操作者桶 |
| 浏览器长令牌消除 | 完成 | Angular Token 仅内存保存 |

## 验证记录

- `dotnet build Services/GuiyangMahjong.Services.slnx --configuration Release`：通过，0 warning / 0 error。
- 非外部持久化测试：137/137 通过。
- Angular 22 生产构建：通过，输出 `main-5NMRSUVR.js`。
- Docker Compose 配置解析：通过。
- 隔离 PostgreSQL 17 实际授权测试：通过。
  - Auth 读取自身表：允许。
  - Auth 读取 PlayerData：拒绝。
  - Auth 执行 CREATE TABLE：拒绝。
  - audit writer UPDATE 审计账本：拒绝。
  - archive 读取/更新归档 Outbox：允许。
  - archive 读取原始审计账本：拒绝。
- 临时验证容器 `mahjong-workflow-e-postgres` 已停止并自动删除。

## 发布注意事项

当前 `guiyang-mahjong-*` 容器仍使用本轮开始前的共享数据库配置。为避免中断正在运行的大厅、玩家和房间服务，本轮没有直接对该数据库执行所有权迁移或禁用 `mahjong`。

正式切换必须使用 `Docs/POSTGRES_LEAST_PRIVILEGE_AND_PRODUCTION_IDENTITY.md` 的滚动顺序：先迁移和发放独立 Secret，再滚动服务，最后经过观测窗口后禁用旧账号。生产 Admin 还必须先配置真实企业 OIDC Authority、Audience、MFA 和 HTTPS 入口，否则新的启动门禁会拒绝就绪。
