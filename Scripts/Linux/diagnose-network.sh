#!/usr/bin/env bash
# 诊断 Linux/WSL 部署网络、监听端口、容器路由和外部可达性，只执行只读检查。
# 输出可能包含主机地址，提交为证据前必须脱敏；脚本不得修改防火墙或重启服务。
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd -- "$SCRIPT_DIR/../.." && pwd)"
DEPLOY_DIR="$PROJECT_ROOT/Deploy/linux"
ENV_FILE="${1:-$DEPLOY_DIR/.env}"

if [[ ! -r "$ENV_FILE" ]]; then
  echo "Environment file is not readable: $ENV_FILE" >&2
  exit 1
fi

read_env() {
  local key="$1"
  awk -F= -v key="$key" '$1 == key {sub(/^[^=]*=/, ""); print; exit}' "$ENV_FILE"
}

advertised_ip="$(read_env ADVERTISED_IP)"
lobby_port="$(read_env LOBBY_PORT)"; lobby_port="${lobby_port:-18080}"
auth_port="$(read_env AUTH_PORT)"; auth_port="${auth_port:-18082}"
allocator_port="$(read_env ALLOCATOR_PORT)"; allocator_port="${allocator_port:-18081}"
game_start="$(read_env GAME_PORT_START)"; game_start="${game_start:-19000}"
game_end="$(read_env GAME_PORT_END)"; game_end="${game_end:-19099}"

echo "== host =="
printf 'kernel=%s\n' "$(uname -srmo)"
printf 'hostname=%s advertised_ip=%s\n' "$(hostname)" "$advertised_ip"
ip -brief -4 address show scope global || true
ip -4 route || true

echo "== WSL =="
if grep -qi microsoft /proc/sys/kernel/osrelease; then
  printf 'mode=WSL2 systemd=%s\n' "$(systemctl is-system-running 2>/dev/null || true)"
else
  echo 'mode=native-linux'
fi

echo "== Docker Compose =="
if timeout 8 docker info --format 'server={{.ServerVersion}} os={{.OperatingSystem}} root={{.DockerRootDir}}' 2>/dev/null; then
  timeout 8 docker compose --env-file "$ENV_FILE" -f "$DEPLOY_DIR/compose.yaml" ps 2>/dev/null || true
else
  echo 'docker=unavailable'
fi

echo "== listening sockets =="
ss -lntup 2>/dev/null | awk -v a=":$auth_port" -v l=":$lobby_port" -v c=":$allocator_port" \
  -v gs=":$game_start" -v ge=":$game_end" \
  'NR == 1 || index($0,a) || index($0,l) || index($0,c) || index($0,gs) || index($0,ge)'

echo "== readiness =="
failures=0
for endpoint in \
  "auth:http://127.0.0.1:$auth_port/health/ready" \
  "allocator:http://127.0.0.1:$allocator_port/health/ready" \
  "lobby:http://127.0.0.1:$lobby_port/health/ready"; do
  name="${endpoint%%:*}"
  url="${endpoint#*:}"
  if curl --fail --silent --show-error --max-time 5 "$url" >/dev/null; then
    echo "$name=ready"
  else
    echo "$name=unavailable"
    failures=$((failures + 1))
  fi
done

echo "== firewall hints =="
if command -v ufw >/dev/null 2>&1; then ufw status 2>/dev/null || true; fi
if command -v firewall-cmd >/dev/null 2>&1; then firewall-cmd --list-all 2>/dev/null || true; fi
printf 'required_tcp=%s,%s required_udp=%s-%s\n' "$auth_port" "$lobby_port" "$game_start" "$game_end"
echo 'This report intentionally omits tokens, passwords, and signing keys.'

((failures == 0))
