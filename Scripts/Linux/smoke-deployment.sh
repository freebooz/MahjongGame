#!/usr/bin/env bash
# 对已部署 Linux 服务栈执行 Auth、Lobby、Allocator、GameServer 和路由冒烟验证。
# 使用临时测试身份并在结束时清理；任一关键断言失败均返回非零退出码。
set -Eeuo pipefail

ENV_FILE="${1:?environment file is required}"

env_value() {
  local key="$1"
  awk -F= -v key="$key" '$1 == key {sub(/^[^=]*=/, ""); print; exit}' "$ENV_FILE"
}

AUTH_URL="http://127.0.0.1:$(env_value AUTH_PORT)"
LOBBY_URL="http://127.0.0.1:$(env_value LOBBY_PORT)"
ALLOCATOR_URL="http://127.0.0.1:$(env_value ALLOCATOR_PORT)"
INSTALLATION_ID="linux-smoke-$(cat /proc/sys/kernel/random/uuid)"
REQUEST_ID="$(cat /proc/sys/kernel/random/uuid)"
IDEMPOTENCY_KEY="linux-smoke-$REQUEST_ID"

session="$(curl --fail --silent --show-error \
  -H 'Content-Type: application/json' \
  -d "{\"installationId\":\"$INSTALLATION_ID\",\"displayName\":\"LinuxSmoke\"}" \
  "$AUTH_URL/v1/auth/guest")"
access_token="$(jq -er '.accessToken' <<<"$session")"

# 大厅会把客户端版本与协议写入路由票据；冒烟请求必须模拟正式客户端，不能退化为 legacy/0。
room="$(curl --fail --silent --show-error \
  -H "Authorization: Bearer $access_token" \
  -H 'Content-Type: application/json' \
  -H 'X-Client-Version: 1.0.0' \
  -H 'X-Protocol-Version: 1' \
  -H "X-Request-Id: $REQUEST_ID" \
  -H "Idempotency-Key: $IDEMPOTENCY_KEY" \
  -d '{"roundCount":4,"publicRoom":true,"autoStart":true,"passwordProtected":false,"ruleSnapshot":{"ruleId":"GuiyangMainstreamV1"}}' \
  "$LOBBY_URL/v1/rooms")"
room_code="$(jq -er '.roomCode' <<<"$room")"
room_id="$(jq -er '.roomId' <<<"$room")"

deadline=$((SECONDS + 45))
route=""
until route="$(curl --fail --silent --show-error \
  -H "Authorization: Bearer $access_token" \
  -H 'X-Client-Version: 1.0.0' \
  -H 'X-Protocol-Version: 1' \
  -H "X-Request-Id: $(cat /proc/sys/kernel/random/uuid)" \
  "$LOBBY_URL/v1/rooms/$room_code/route" 2>/dev/null)"; do
  ((SECONDS < deadline)) || { echo "Smoke room did not receive a GameServer route." >&2; exit 1; }
  sleep 1
done

server_instance_id="$(jq -er '.serverInstanceId' <<<"$route")"
server_ip="$(jq -er '.serverIp' <<<"$route")"
server_port="$(jq -er '.serverPort' <<<"$route")"
expected_server_ip="$(env_value ADVERTISED_IP)"
[[ "$server_ip" == "$expected_server_ip" && "$server_port" -ge 19000 ]] \
  || { echo "Smoke route contains an invalid advertised endpoint." >&2; exit 1; }
ss -H -lun "sport = :$server_port" | grep -q . \
  || { echo "Smoke route UDP endpoint is not listening: $server_ip:$server_port" >&2; exit 1; }

curl --fail --silent --show-error \
  -X POST \
  -H "Authorization: Bearer $(env_value LOBBY_INTERNAL_TOKEN)" \
  -H 'Content-Type: application/json' \
  -H "X-Request-Id: $(cat /proc/sys/kernel/random/uuid)" \
  -d "{\"serverInstanceId\":\"$server_instance_id\",\"roomId\":\"$room_id\",\"reason\":\"Deployment smoke cleanup\"}" \
  "$LOBBY_URL/internal/gameservers/failure" >/dev/null

curl --fail --silent --show-error \
  -X POST \
  -H "Authorization: Bearer $(env_value ALLOCATOR_SERVICE_TOKEN)" \
  -H "X-Request-Id: $(cat /proc/sys/kernel/random/uuid)" \
  "$ALLOCATOR_URL/internal/instances/$server_instance_id/drain" >/dev/null

echo "SMOKE_OK roomCode=$room_code server=$server_ip:$server_port"
