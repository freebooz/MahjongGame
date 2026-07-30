#!/usr/bin/env bash
# 在隔离环境验证部署升级、备份和回滚流程，不得指向生产数据卷或真实密钥。
# 测试需确认版本指针、服务健康和数据保持语义，并在失败时保留诊断。
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd -- "$SCRIPT_DIR/../.." && pwd)"
DEPLOY_SCRIPT="$PROJECT_ROOT/Deploy/linux/deploy.sh"
ENV_FILE="${1:-$PROJECT_ROOT/Deploy/linux/.env}"
CURRENT_FILE="$PROJECT_ROOT/Deploy/linux/.deployed-version"

[[ $EUID -eq 0 ]] || { echo 'Run this integration test with sudo.' >&2; exit 1; }
[[ -s "$CURRENT_FILE" ]] || { echo 'A successful deployment is required first.' >&2; exit 1; }
current="$(<"$CURRENT_FILE")"
probe="rollback-probe-missing-$(date +%s)"
log="$(mktemp)"
trap 'rm -f "$log"' EXIT

set +e
timeout 180 "$DEPLOY_SCRIPT" upgrade --env-file "$ENV_FILE" --pull-only --version "$probe" >"$log" 2>&1
exit_code=$?
set -e
cat "$log"

((exit_code != 0)) || { echo 'The missing-version deployment unexpectedly succeeded.' >&2; exit 1; }
grep -q "AUTO_ROLLBACK_OK version=$current" "$log" \
  || { echo 'The deployment did not report a successful automatic rollback.' >&2; exit 1; }
actual="$(awk -F= '$1 == "MAHJONG_VERSION" {print $2; exit}' "$ENV_FILE")"
[[ "$actual" == "$current" ]] || { echo "Expected version $current after rollback; found $actual." >&2; exit 1; }
"$DEPLOY_SCRIPT" status --env-file "$ENV_FILE"
echo "ROLLBACK_TEST_OK restored=$current failedVersion=$probe"
