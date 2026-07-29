#!/usr/bin/env bash
set -euo pipefail

# 在 CI 的独立 PostgreSQL 中创建业务结构、应用最小授权，并以真实登录身份验证允许与拒绝路径。
root_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
admin_url="${LEAST_PRIVILEGE_ADMIN_URL:?LEAST_PRIVILEGE_ADMIN_URL is required}"

psql "$admin_url" -v ON_ERROR_STOP=1 \
  -f "$root_dir/Deploy/postgres/least-privilege/001_roles.sql"
psql "$admin_url" -v ON_ERROR_STOP=1 \
  -f "$root_dir/Services/GuiyangMahjong.Auth/Storage/schema.sql"
psql "$admin_url" -v ON_ERROR_STOP=1 \
  -f "$root_dir/Services/GuiyangMahjong.Lobby/Storage/schema.sql"
psql "$admin_url" -v ON_ERROR_STOP=1 \
  -f "$root_dir/Services/GuiyangMahjong.PlayerData/Storage/schema.sql"
psql "$admin_url" -v ON_ERROR_STOP=1 \
  -f "$root_dir/Services/GuiyangMahjong.Admin/Storage/schema.sql"
psql "$admin_url" -v ON_ERROR_STOP=1 \
  -f "$root_dir/Deploy/postgres/least-privilege/002_grants.sql"

# CI 密码仅存在于临时数据库，不对应任何环境；同时验证生产密码注入脚本可执行。
psql "$admin_url" -v ON_ERROR_STOP=1 \
  -v migration_password=ci-migration-password-only \
  -v auth_password=ci-auth-password-only \
  -v lobby_password=ci-lobby-password-only \
  -v player_data_password=ci-player-password-only \
  -v admin_password=ci-admin-password-only \
  -v monitor_password=ci-monitor-password-only \
  -v audit_password=ci-audit-password-only \
  -v archive_password=ci-archive-password-only \
  -f "$root_dir/Deploy/postgres/least-privilege/004_set_login_passwords.sql"

database_name="$(psql "$admin_url" -Atc 'select current_database()')"
host_name="${PGHOST:-127.0.0.1}"
port_number="${PGPORT:-5432}"

PGPASSWORD=ci-auth-password-only psql \
  "host=$host_name port=$port_number dbname=$database_name user=mahjong_auth options='-c role=mahjong_auth_rw'" \
  -v ON_ERROR_STOP=1 -c 'SELECT count(*) FROM auth_identities' >/dev/null

# 越权查询和 DDL 必须由 PostgreSQL 以 insufficient_privilege 拒绝。
if PGPASSWORD=ci-auth-password-only psql \
  "host=$host_name port=$port_number dbname=$database_name user=mahjong_auth options='-c role=mahjong_auth_rw'" \
  -v ON_ERROR_STOP=1 -c 'SELECT count(*) FROM player_data.wallet_balances' >/dev/null 2>&1; then
  echo "Auth 身份意外读取了 PlayerData schema" >&2
  exit 1
fi
if PGPASSWORD=ci-auth-password-only psql \
  "host=$host_name port=$port_number dbname=$database_name user=mahjong_auth options='-c role=mahjong_auth_rw'" \
  -v ON_ERROR_STOP=1 -c 'CREATE TABLE auth_forbidden_ddl(id integer)' >/dev/null 2>&1; then
  echo "Auth 运行身份意外获得 DDL" >&2
  exit 1
fi
if PGPASSWORD=ci-audit-password-only psql \
  "host=$host_name port=$port_number dbname=$database_name user=mahjong_audit_writer options='-c role=mahjong_audit_append'" \
  -v ON_ERROR_STOP=1 -c "UPDATE admin_monitor.audit_ledger SET reason='forbidden'" >/dev/null 2>&1; then
  echo "审计追加身份意外获得 UPDATE" >&2
  exit 1
fi
PGPASSWORD=ci-archive-password-only psql \
  "host=$host_name port=$port_number dbname=$database_name user=mahjong_archive options='-c role=mahjong_archive_dispatch'" \
  -v ON_ERROR_STOP=1 -c 'SELECT count(*) FROM admin_monitor.audit_archive_outbox' >/dev/null
if PGPASSWORD=ci-archive-password-only psql \
  "host=$host_name port=$port_number dbname=$database_name user=mahjong_archive options='-c role=mahjong_archive_dispatch'" \
  -v ON_ERROR_STOP=1 -c 'SELECT count(*) FROM admin_monitor.audit_ledger' >/dev/null 2>&1; then
  echo "归档派发身份意外读取了审计原始账本" >&2
  exit 1
fi

echo "PostgreSQL 最小权限真实连接验证通过。"
