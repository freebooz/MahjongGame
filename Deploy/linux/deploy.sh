#!/usr/bin/env bash
# Linux 生产部署统一入口：负责首次安装、升级、状态检查、诊断、备份、回滚和恢复。
# 脚本以不可变版本和 Artifacts/LinuxServer 为输入，生产密钥只从权限受控的 .env 读取。
# 所有破坏性子命令必须显式确认并保留数据卷；失败时通过 ERR trap 输出可关联诊断信息。
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd -- "$SCRIPT_DIR/../.." && pwd)"
COMPOSE_FILE="$SCRIPT_DIR/compose.yaml"
ENV_FILE="$SCRIPT_DIR/.env"
ACTION="${1:-}"
shift || true

VERSION=""
BACKUP_FILE=""
BOOTSTRAP=false
PULL_ONLY=false
REFRESH_BASE_IMAGES=false
REFRESH_ADVERTISED_IP=false
CONFIRM=false
DEPLOY_STARTED=false
ACTIVE_VERSION_BEFORE=""
DOCKER_RESTART_REQUIRED=false

while (($#)); do
  case "$1" in
    --version) VERSION="${2:?--version needs a value}"; shift 2 ;;
    --env-file) ENV_FILE="$(realpath -m "${2:?--env-file needs a value}")"; shift 2 ;;
    --backup-file) BACKUP_FILE="$(realpath -m "${2:?--backup-file needs a value}")"; shift 2 ;;
    --bootstrap) BOOTSTRAP=true; shift ;;
    --pull-only) PULL_ONLY=true; shift ;;
    --refresh-base-images) REFRESH_BASE_IMAGES=true; shift ;;
    --refresh-advertised-ip) REFRESH_ADVERTISED_IP=true; shift ;;
    --confirm) CONFIRM=true; shift ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

usage() {
  cat <<'EOF'
Usage: sudo ./Deploy/linux/deploy.sh ACTION [options]

Actions:
  install|upgrade   Deploy all Linux server applications.
  rollback          Restore the previous application image version.
  status            Show container and endpoint health.
  doctor            Validate host, configuration, ports and Compose.
  backup            Back up PostgreSQL and allocator durable files.
  restore           Restore a backup selected with --backup-file and --confirm.

Options:
  --version VERSION       Immutable image/build version.
  --env-file PATH         Alternate root-only environment file.
  --bootstrap             Install Docker Engine when it is missing.
  --pull-only             Pull prebuilt images instead of building locally.
  --refresh-base-images   Refresh Dockerfile base-image metadata before a local build.
  --refresh-advertised-ip Refresh ADVERTISED_IP from the host's current primary IPv4.
  --backup-file PATH      Backup archive for restore.
  --confirm               Confirm destructive restore.
EOF
}

if [[ -z "$ACTION" ]]; then usage; exit 2; fi
case "$ACTION" in
  install|upgrade|rollback|status|doctor|backup|restore) ;;
  *) usage; exit 2 ;;
esac

if [[ $EUID -ne 0 ]]; then
  echo "Run this deployment entry point as root (sudo)." >&2
  exit 1
fi

mkdir -p /var/lock
exec 9>/var/lock/guiyang-mahjong-deploy.lock
if ! flock -n 9; then
  echo "Another Guiyang Mahjong deployment is already running." >&2
  exit 1
fi

compose() {
  # Docker Compose gives the parent shell higher precedence than --env-file.
  # Remove only keys declared by the root-owned deployment file so stale login
  # profiles or CI variables cannot silently split service credentials.
  local -a clean_environment=(env)
  local key
  while IFS='=' read -r key _; do
    [[ "$key" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || continue
    clean_environment+=(-u "$key")
  done <"$ENV_FILE"
  "${clean_environment[@]}" docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" "$@"
}

env_value() {
  local key="$1"
  awk -F= -v key="$key" '$1 == key {sub(/^[^=]*=/, ""); print; exit}' "$ENV_FILE"
}

random_secret() {
  openssl rand -hex "${1:-32}"
}

detect_advertised_ip() {
  ip -4 route get 1.1.1.1 2>/dev/null \
    | awk '{for (i=1; i<=NF; i++) if ($i == "src") {print $(i+1); exit}}'
}

bootstrap_docker() {
  export DEBIAN_FRONTEND=noninteractive
  apt-get update
  apt-get install --yes ca-certificates curl gnupg jq openssl util-linux
  install -m 0755 -d /etc/apt/keyrings
  if [[ ! -s /etc/apt/keyrings/docker.asc ]]; then
    curl --fail --silent --show-error --location https://download.docker.com/linux/ubuntu/gpg \
      --output /etc/apt/keyrings/docker.asc
    chmod a+r /etc/apt/keyrings/docker.asc
  fi
  local distro_codename
  distro_codename="$(awk -F= '$1 == "VERSION_CODENAME" {gsub(/\"/, "", $2); print $2}' /etc/os-release)"
  [[ -n "$distro_codename" ]] || { echo "Could not determine Ubuntu codename." >&2; exit 1; }
  local architecture
  architecture="$(dpkg --print-architecture)"
  printf 'Types: deb\nURIs: https://download.docker.com/linux/ubuntu\nSuites: %s\nComponents: stable\nArchitectures: %s\nSigned-By: /etc/apt/keyrings/docker.asc\n' \
    "$distro_codename" "$architecture" >/etc/apt/sources.list.d/docker.sources
  apt-get update || true
  if apt-cache show docker-ce >/dev/null 2>&1; then
    apt-get install --yes docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
  else
    echo "Docker upstream repository is unavailable; using Ubuntu-maintained Docker packages." >&2
    rm -f /etc/apt/sources.list.d/docker.sources
    apt-get update
    apt-get install --yes docker.io docker-compose-v2 docker-buildx
  fi
  systemctl enable --now docker
}

configure_docker_proxy() {
  local proxy="${HTTPS_PROXY:-${https_proxy:-${HTTP_PROXY:-${http_proxy:-}}}}"
  [[ -n "$proxy" ]] || return 0
  local directory=/etc/systemd/system/docker.service.d
  local target="$directory/proxy.conf"
  local temporary
  temporary="$(mktemp)"
  printf '[Service]\nEnvironment="HTTP_PROXY=%s"\nEnvironment="HTTPS_PROXY=%s"\nEnvironment="NO_PROXY=localhost,127.0.0.1,::1,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16"\n' \
    "$proxy" "$proxy" >"$temporary"
  install -d -m 0755 "$directory"
  if [[ ! -f "$target" ]] || ! cmp --silent "$temporary" "$target"; then
    install -m 0644 "$temporary" "$target"
    systemctl daemon-reload
    DOCKER_RESTART_REQUIRED=true
  fi
  rm -f "$temporary"
}

configure_docker_daemon() {
  local directory=/etc/docker
  local target="$directory/daemon.json"
  local temporary current
  temporary="$(mktemp)"
  current='{}'
  if [[ -s "$target" ]]; then
    jq empty "$target" || { echo "Invalid Docker configuration: $target" >&2; exit 1; }
    current="$(cat "$target")"
  fi
  jq '. + {"userland-proxy": false}' <<<"$current" >"$temporary"
  install -d -m 0755 "$directory"
  if [[ ! -f "$target" ]] || ! cmp --silent "$temporary" "$target"; then
    install -m 0644 "$temporary" "$target"
    DOCKER_RESTART_REQUIRED=true
  fi
  rm -f "$temporary"
}

ensure_tools() {
  local missing=()
  for command in curl jq openssl flock ip awk sed tar gzip timeout; do
    command -v "$command" >/dev/null 2>&1 || missing+=("$command")
  done
  if ((${#missing[@]})); then
    if [[ "$BOOTSTRAP" == true ]]; then
      export DEBIAN_FRONTEND=noninteractive
      apt-get update
      apt-get install --yes curl jq openssl util-linux iproute2 gawk sed tar gzip coreutils
    else
      echo "Missing required commands: ${missing[*]}. Re-run with --bootstrap." >&2
      exit 1
    fi
  fi

  if ! command -v docker >/dev/null 2>&1 || ! docker compose version >/dev/null 2>&1; then
    if [[ "$BOOTSTRAP" == true ]]; then bootstrap_docker; else
      echo "Docker Engine with Compose v2 is required. Re-run with --bootstrap." >&2
      exit 1
    fi
  fi
  if [[ "$BOOTSTRAP" == true ]]; then
    configure_docker_proxy
    configure_docker_daemon
    [[ "$DOCKER_RESTART_REQUIRED" == false ]] || systemctl restart docker
  fi
  if ! timeout 5 docker info >/dev/null 2>&1; then
    systemctl start docker 2>/dev/null || true
    local deadline=$((SECONDS + 240))
    until timeout 5 docker info >/dev/null 2>&1; do
      ((SECONDS < deadline)) || { echo "Docker Engine did not become ready within 240 seconds." >&2; exit 1; }
      sleep 2
    done
  fi
}

ensure_environment() {
  if [[ ! -f "$ENV_FILE" ]]; then
    local advertised_ip
    advertised_ip="$(detect_advertised_ip)"
    [[ -n "$advertised_ip" ]] || { echo "Could not detect an advertised IPv4 address." >&2; exit 1; }
    umask 077
    cat >"$ENV_FILE" <<EOF
MAHJONG_VERSION=${VERSION:-dev}
IMAGE_REGISTRY=local
GAME_SERVER_MAP=/Game/Maps/MahjongRoomMap?game=/Script/GuiyangMahjongServer.GuiyangMahjongGameMode
MAHJONG_DATA_ROOT=/var/lib/guiyang-mahjong
ADVERTISED_IP=$advertised_ip
AUTH_PORT=18082
LOBBY_PORT=18080
ALLOCATOR_PORT=18081
GAME_PORT_START=19000
GAME_PORT_END=19099
GAMEDATA_PORT=18088
POSTGRES_PASSWORD=$(random_secret 24)
PLAYER_TOKEN_SIGNING_KEY=$(random_secret 32)
GUEST_IDENTITY_PEPPER=$(random_secret 32)
JOIN_TICKET_SIGNING_KEY=$(random_secret 32)
LOBBY_INTERNAL_TOKEN=$(random_secret 32)
ALLOCATOR_SERVICE_TOKEN=$(random_secret 32)
EOF
  fi

  # 历史版本曾让 GameData 与玩家入口 EdgeGateway 共用 18085；升级时自动迁移仅本机诊断端口。
  if [[ "$(env_value GAMEDATA_PORT)" == "18085" ]]; then
    sed -i 's/^GAMEDATA_PORT=18085$/GAMEDATA_PORT=18088/' "$ENV_FILE"
  fi
  if [[ -z "$(env_value GAMEDATA_PORT)" ]]; then
    printf '%s\n' 'GAMEDATA_PORT=18088' >>"$ENV_FILE"
  fi

  # Keep existing installations forward-compatible when a newer Compose file
  # introduces another authenticated management or monitoring endpoint.  An
  # upgrade must backfill a strong, unique token instead of failing halfway
  # through the deployment or encouraging operators to disable authentication.
  local secret_key
  # 与 compose.yaml 的 :? 必填机密保持同源：首次部署和升级补齐缺失项，
  # 避免新架构增加服务后只在 docker compose 插值阶段才暴露缺失配置。
  local managed_secrets=(
    POSTGRES_PASSWORD
    MIGRATION_DB_PASSWORD
    AUTH_DB_PASSWORD
    LOBBY_DB_PASSWORD
    GAME_DATA_DB_PASSWORD
    ECONOMY_DB_PASSWORD
    CONFIGURATION_DB_PASSWORD
    WORKERS_DB_PASSWORD
    ADMIN_DB_PASSWORD
    MONITOR_DB_PASSWORD
    AUDIT_DB_PASSWORD
    ARCHIVE_DB_PASSWORD
    NATS_USERNAME
    NATS_PASSWORD
    PLAYER_TOKEN_SIGNING_KEY
    GUEST_IDENTITY_PEPPER
    JOIN_TICKET_SIGNING_KEY
    SETTLEMENT_SIGNING_KEY
    LOBBY_INTERNAL_TOKEN
    ALLOCATOR_SERVICE_TOKEN
    ALLOCATOR_MANAGEMENT_COMMAND_TOKEN
    AUTH_MANAGEMENT_COMMAND_TOKEN
    LOBBY_MANAGEMENT_COMMAND_TOKEN
    MONITORING_READ_ONLY_TOKEN
    CONFIGURATION_SIGNING_KEY
    CONFIGURATION_ADMIN_TOKEN
    CONFIGURATION_READ_TOKEN
    ADMIN_TOPOLOGY_REGISTRATION_TOKEN
    ADMIN_TOPOLOGY_LOBBY_TOKEN
    ADMIN_TOPOLOGY_ALLOCATOR_TOKEN
    GAMEDATA_LOBBY_AUTHORITY_TOKEN
    GAMEDATA_MONITORING_TOKEN
    GAMEDATA_RECOVERY_TOKEN
    PLAYER_DATA_ADMIN_COMMAND_TOKEN
    PLAYER_DATA_CHAT_GATEWAY_TOKEN
    PLAYER_DATA_MONITORING_TOKEN
    PLAYER_DATA_SOURCE_INGESTION_TOKEN
    ECONOMY_DB_PASSWORD
    ECONOMY_SOURCE_TOKEN
    ECONOMY_ADMIN_TOKEN
    ECONOMY_MONITORING_TOKEN
    COMMUNITY_CHAT_GATEWAY_TOKEN
    COMMUNITY_PLAYERDATA_TOKEN
    ADMIN_CHAT_COMPLIANCE_TOKEN
    ADMIN_COMPENSATION_OPERATOR_TOKEN
    ADMIN_EVIDENCE_INGESTION_TOKEN
    ADMIN_INFRASTRUCTURE_OPERATOR_TOKEN
    ADMIN_PLAYER_APPROVER_TOKEN
    ADMIN_PLAYER_OPERATOR_TOKEN
    ADMIN_READ_ONLY_TOKEN
    ADMIN_RISK_ANALYST_TOKEN
    ADMIN_ROOM_APPROVER_TOKEN
    ADMIN_ROOM_OPERATOR_TOKEN
    ADMIN_SANCTION_OPERATOR_TOKEN
    ADMIN_SUPPORT_OPERATOR_TOKEN
  )
  for secret_key in "${managed_secrets[@]}"; do
    if [[ -z "$(env_value "$secret_key")" ]]; then
      printf '%s=%s\n' "$secret_key" "$(random_secret 32)" >>"$ENV_FILE"
    fi
  done

  if [[ "$REFRESH_ADVERTISED_IP" == true ]]; then
    local refreshed_advertised_ip
    refreshed_advertised_ip="$(detect_advertised_ip)"
    [[ -n "$refreshed_advertised_ip" ]] \
      || { echo "Could not refresh the advertised IPv4 address." >&2; exit 1; }
    if grep -q '^ADVERTISED_IP=' "$ENV_FILE"; then
      sed -i "s/^ADVERTISED_IP=.*/ADVERTISED_IP=$refreshed_advertised_ip/" "$ENV_FILE"
    else
      printf 'ADVERTISED_IP=%s\n' "$refreshed_advertised_ip" >>"$ENV_FILE"
    fi
    echo "Refreshed ADVERTISED_IP=$refreshed_advertised_ip"
  fi
  chown root:root "$ENV_FILE"
  chmod 0600 "$ENV_FILE"

  if [[ -n "$VERSION" ]]; then
    [[ "$VERSION" =~ ^[A-Za-z0-9._-]+$ ]] || { echo "Invalid version: $VERSION" >&2; exit 2; }
    if grep -q '^MAHJONG_VERSION=' "$ENV_FILE"; then
      sed -i "s/^MAHJONG_VERSION=.*/MAHJONG_VERSION=$VERSION/" "$ENV_FILE"
    else
      printf 'MAHJONG_VERSION=%s\n' "$VERSION" >>"$ENV_FILE"
    fi
  fi
}

validate_environment() {
  # 此列表必须覆盖 compose.yaml 中所有 :? 变量，以便在构建镜像前给出可读错误，
  # 同时保证新服务的数据库账号、NATS 账号和用途隔离令牌不会漏配。
  local required=(MAHJONG_VERSION MAHJONG_DATA_ROOT ADVERTISED_IP GAME_SERVER_MAP \
    POSTGRES_PASSWORD MIGRATION_DB_PASSWORD AUTH_DB_PASSWORD LOBBY_DB_PASSWORD \
    GAME_DATA_DB_PASSWORD ECONOMY_DB_PASSWORD CONFIGURATION_DB_PASSWORD WORKERS_DB_PASSWORD \
    ADMIN_DB_PASSWORD MONITOR_DB_PASSWORD AUDIT_DB_PASSWORD ARCHIVE_DB_PASSWORD \
    NATS_USERNAME NATS_PASSWORD PLAYER_TOKEN_SIGNING_KEY GUEST_IDENTITY_PEPPER \
    JOIN_TICKET_SIGNING_KEY SETTLEMENT_SIGNING_KEY LOBBY_INTERNAL_TOKEN \
    ALLOCATOR_SERVICE_TOKEN ALLOCATOR_MANAGEMENT_COMMAND_TOKEN AUTH_MANAGEMENT_COMMAND_TOKEN \
    LOBBY_MANAGEMENT_COMMAND_TOKEN MONITORING_READ_ONLY_TOKEN CONFIGURATION_SIGNING_KEY \
    CONFIGURATION_ADMIN_TOKEN CONFIGURATION_READ_TOKEN ADMIN_TOPOLOGY_REGISTRATION_TOKEN \
    ADMIN_TOPOLOGY_LOBBY_TOKEN ADMIN_TOPOLOGY_ALLOCATOR_TOKEN GAMEDATA_LOBBY_AUTHORITY_TOKEN \
    GAMEDATA_MONITORING_TOKEN GAMEDATA_RECOVERY_TOKEN ECONOMY_SOURCE_TOKEN ECONOMY_ADMIN_TOKEN \
    ECONOMY_MONITORING_TOKEN COMMUNITY_CHAT_GATEWAY_TOKEN ADMIN_READ_ONLY_TOKEN \
    ADMIN_EVIDENCE_INGESTION_TOKEN ADMIN_CHAT_COMPLIANCE_TOKEN ADMIN_COMPENSATION_OPERATOR_TOKEN \
    ADMIN_INFRASTRUCTURE_OPERATOR_TOKEN ADMIN_PLAYER_APPROVER_TOKEN ADMIN_PLAYER_OPERATOR_TOKEN \
    ADMIN_RISK_ANALYST_TOKEN ADMIN_ROOM_APPROVER_TOKEN ADMIN_ROOM_OPERATOR_TOKEN \
    ADMIN_SANCTION_OPERATOR_TOKEN ADMIN_SUPPORT_OPERATOR_TOKEN)
  local key value
  for key in "${required[@]}"; do
    value="$(env_value "$key")"
    [[ -n "$value" ]] || { echo "Missing $key in $ENV_FILE" >&2; exit 1; }
    if [[ "$key" != MAHJONG_VERSION && "$key" != MAHJONG_DATA_ROOT && "$key" != ADVERTISED_IP \
          && "$key" != GAME_SERVER_MAP \
          && ${#value} -lt 32 ]]; then
      echo "$key must contain at least 32 characters." >&2
      exit 1
    fi
  done
  local advertised_ip game_server_map port_start port_end
  advertised_ip="$(env_value ADVERTISED_IP)"
  [[ "$advertised_ip" != "0.0.0.0" && "$advertised_ip" != "::" ]] \
    || { echo "ADVERTISED_IP must be a client-reachable address." >&2; exit 1; }
  game_server_map="$(env_value GAME_SERVER_MAP)"
  if [[ "$game_server_map" != "/Game/Maps/MahjongRoomMap?game=/Script/GuiyangMahjongServer.GuiyangMahjongGameMode" ]]; then
    echo "GAME_SERVER_MAP must select MahjongRoomMap and inject GuiyangMahjongGameMode." >&2
    exit 1
  fi
  if [[ "$advertised_ip" == 127.* || "$advertised_ip" == "::1" ]]; then
    echo "A real UE server cannot advertise a loopback address to external clients." >&2
    exit 1
  fi
  port_start="$(env_value GAME_PORT_START)"; port_start="${port_start:-19000}"
  port_end="$(env_value GAME_PORT_END)"; port_end="${port_end:-19099}"
  [[ "$port_start" =~ ^[0-9]+$ && "$port_end" =~ ^[0-9]+$ \
     && $port_start -ge 1024 && $port_end -le 65535 && $port_end -ge $port_start \
     && $((port_end - port_start + 1)) -le 1000 ]] \
    || { echo "GAME_PORT_START/GAME_PORT_END must define at most 1000 valid user ports." >&2; exit 1; }
  compose config --quiet
}

prepare_data_directories() {
  local data_root
  data_root="$(env_value MAHJONG_DATA_ROOT)"
  [[ "$data_root" = /* && "$data_root" != "/" ]] \
    || { echo "MAHJONG_DATA_ROOT must be a non-root absolute path." >&2; exit 1; }
  install -d -m 0750 "$data_root" "$data_root/allocator" \
    "$data_root/allocator/state" "$data_root/allocator/outbox" \
    "$data_root/backups" "$data_root/diagnostics"
  chown -R 1654:1654 "$data_root/allocator"
}

port_available() {
  local protocol="$1" port="$2"
  if [[ "$protocol" == tcp ]]; then
    if ss -H -ltn "sport = :$port" | grep -q .; then return 1; fi
  else
    if ss -H -lun "sport = :$port" | grep -q .; then return 1; fi
  fi
  return 0
}

compose_owns_port() {
  local protocol="$1" port="$2"
  if docker ps \
    --filter label=com.docker.compose.project=guiyang-mahjong \
    --filter "publish=$port/$protocol" \
    --quiet | grep -q .; then
    return 0
  fi

  # The real UE game-node uses host networking so that the allocator can bind
  # its control port and dynamically allocate the UDP game-port range. Docker
  # does not report host-network sockets through the `publish` filter above.
  # Treat only this Compose project's running game-node as the owner of the
  # configured allocator port; all other occupied ports remain hard failures.
  [[ "$protocol" == tcp && "$port" == "$(env_value ALLOCATOR_PORT)" ]] || return 1
  docker ps \
    --filter label=com.docker.compose.project=guiyang-mahjong \
    --filter label=com.docker.compose.service=game-node \
    --filter network=host \
    --quiet | grep -q .
}

doctor() {
  ensure_tools
  ensure_environment
  validate_environment
  prepare_data_directories
  [[ "$(uname -m)" == x86_64 ]] || { echo "Only x86_64 Linux is currently supported." >&2; exit 1; }
  local cpu_count memory_kib free_kib data_root
  cpu_count="$(nproc)"
  memory_kib="$(awk '/^MemTotal:/ {print $2}' /proc/meminfo)"
  data_root="$(env_value MAHJONG_DATA_ROOT)"
  free_kib="$(df -Pk "$data_root" | awk 'NR == 2 {print $4}')"
  ((cpu_count >= 2)) || { echo "At least 2 logical CPUs are required." >&2; exit 1; }
  ((memory_kib >= 4 * 1024 * 1024)) || { echo "At least 4 GiB RAM is required." >&2; exit 1; }
  ((free_kib >= 10 * 1024 * 1024)) || { echo "At least 10 GiB free space is required below $data_root." >&2; exit 1; }
  if [[ -r /proc/version ]] && grep -qi microsoft /proc/version; then
    [[ "$(ps -p 1 -o comm= | xargs)" == systemd ]] \
      || { echo "WSL must run with systemd enabled." >&2; exit 1; }
  fi
  for port in "$(env_value AUTH_PORT)" "$(env_value LOBBY_PORT)" "$(env_value ALLOCATOR_PORT)"; do
    [[ -z "$port" ]] || port_available tcp "$port" || compose_owns_port tcp "$port" \
      || { echo "TCP port $port is already occupied." >&2; exit 1; }
  done
  echo "DOCTOR_OK"
}

wait_endpoint() {
  local name="$1" url="$2" deadline=$((SECONDS + 180))
  until curl --fail --silent --show-error --max-time 5 "$url" >/dev/null 2>&1; do
    if ((SECONDS >= deadline)); then
      echo "$name did not become ready: $url" >&2
      compose ps >&2 || true
      return 1
    fi
    sleep 2
  done
}

reconcile_postgres_credentials() {
  local restart_dependents="${1:-true}" deadline=$((SECONDS + 60)) password
  until compose exec -T postgres pg_isready -U mahjong -d mahjong >/dev/null 2>&1; do
    ((SECONDS < deadline)) \
      || { echo "PostgreSQL did not become ready for credential reconciliation." >&2; return 1; }
    sleep 1
  done
  password="$(env_value POSTGRES_PASSWORD)"
  # POSTGRES_PASSWORD initializes an empty volume but does not update an existing role.
  # Feed the desired value through stdin so it never appears in a process argument or log.
  printf "\\set role_password '%s'\nALTER ROLE mahjong WITH PASSWORD :'role_password';\n" "$password" \
    | compose exec -T -u postgres postgres \
        psql --no-psqlrc --set=ON_ERROR_STOP=1 --username=mahjong --dbname=mahjong >/dev/null
  # 迁移前仅需统一数据库登录凭据；Auth/Lobby 尚未创建时不能执行 restart。
  if [[ "$restart_dependents" == true ]]; then
    compose restart auth lobby >/dev/null
  fi
  echo "POSTGRES_CREDENTIALS_OK"
}

collect_diagnostics() {
  [[ -f "$ENV_FILE" ]] || return 0
  local data_root stamp target
  data_root="$(env_value MAHJONG_DATA_ROOT)"
  stamp="$(date -u +%Y%m%dT%H%M%SZ)"
  target="$data_root/diagnostics/$stamp"
  mkdir -p "$target"
  compose ps >"$target/compose-ps.txt" 2>&1 || true
  compose logs --no-color --tail 500 >"$target/compose.log" 2>&1 || true
  timeout 10 docker info >"$target/docker-info.txt" 2>&1 || true
  echo "Diagnostics: $target" >&2
}

deploy() {
  doctor
  local previous current
  current="$(env_value MAHJONG_VERSION)"
  previous=""
  [[ -f "$SCRIPT_DIR/.deployed-version" ]] && previous="$(<"$SCRIPT_DIR/.deployed-version")"
  ACTIVE_VERSION_BEFORE="$previous"
  DEPLOY_STARTED=true
  if [[ -n "$previous" && "$previous" != "$current" ]]; then
    backup
  fi
  [[ -z "$previous" || "$previous" == "$current" ]] || printf '%s\n' "$previous" >"$SCRIPT_DIR/.previous-version"

  if [[ "$PULL_ONLY" == true ]]; then
    compose pull
  else
    local build_arguments=(build)
    [[ "$REFRESH_BASE_IMAGES" == false ]] || build_arguments+=(--pull)
    BUILDKIT_PROGRESS=plain compose "${build_arguments[@]}"
  fi
  # PostgreSQL 数据卷可能来自上一版本；先以数据库超级用户轮换 mahjong 登录密码，
  # 再启动依赖该登录身份的迁移容器，避免新 .env 与旧持久卷密码不一致而中断升级。
  compose up --detach --force-recreate postgres
  reconcile_postgres_credentials false
  # 升级必须重建全部容器：配置文件挂载类型、Linux DS 产物和环境变量都可能变化；
  # 仅依赖 Compose 的配置摘要会保留旧容器，从而继续使用已失效的 bind mount。
  compose up --detach --force-recreate --remove-orphans
  reconcile_postgres_credentials
  wait_endpoint Auth "http://127.0.0.1:$(env_value AUTH_PORT)/health/ready"
  wait_endpoint Allocator "http://127.0.0.1:$(env_value ALLOCATOR_PORT)/health/ready"
  wait_endpoint Lobby "http://127.0.0.1:$(env_value LOBBY_PORT)/health/ready"
  "$PROJECT_ROOT/Scripts/Linux/smoke-deployment.sh" "$ENV_FILE"
  printf '%s\n' "$current" >"$SCRIPT_DIR/.deployed-version"
  chmod 0600 "$SCRIPT_DIR/.deployed-version"
  DEPLOY_STARTED=false
  echo "DEPLOYMENT_OK version=$current"
}

status() {
  ensure_tools
  ensure_environment
  validate_environment
  compose ps
  wait_endpoint Auth "http://127.0.0.1:$(env_value AUTH_PORT)/health/ready"
  wait_endpoint Allocator "http://127.0.0.1:$(env_value ALLOCATOR_PORT)/health/ready"
  wait_endpoint Lobby "http://127.0.0.1:$(env_value LOBBY_PORT)/health/ready"
  echo "STATUS_OK"
}

backup() {
  status >/dev/null
  local data_root stamp target temporary
  data_root="$(env_value MAHJONG_DATA_ROOT)"
  stamp="$(date -u +%Y%m%dT%H%M%SZ)"
  target="$data_root/backups/guiyang-mahjong-$stamp.tar.gz"
  temporary="$(mktemp -d)"
  trap 'rm -rf "$temporary"' RETURN
  compose exec -T postgres pg_dump -U mahjong -d mahjong --format=custom >"$temporary/postgres.dump"
  tar -C "$data_root" -czf "$temporary/allocator.tar.gz" allocator
  cp "$ENV_FILE" "$temporary/environment.snapshot"
  chmod 0600 "$temporary/environment.snapshot"
  tar -C "$temporary" -czf "$target" postgres.dump allocator.tar.gz environment.snapshot
  chmod 0600 "$target"
  rm -rf "$temporary"
  trap - RETURN
  echo "BACKUP_OK path=$target"
}

rollback() {
  ensure_tools
  ensure_environment
  local target="${VERSION:-}" current missing image
  [[ -n "$target" ]] || [[ ! -f "$SCRIPT_DIR/.previous-version" ]] || target="$(<"$SCRIPT_DIR/.previous-version")"
  [[ -n "$target" ]] || { echo "No previous version is recorded; pass --version." >&2; exit 1; }
  current=""
  [[ ! -f "$SCRIPT_DIR/.deployed-version" ]] || current="$(<"$SCRIPT_DIR/.deployed-version")"
  VERSION="$target"
  ensure_environment
  validate_environment
  prepare_data_directories
  missing=false
  while IFS= read -r image; do
    [[ -z "$image" ]] || docker image inspect "$image" >/dev/null 2>&1 || missing=true
  done < <(compose config --images | sort -u)
  [[ "$missing" == false ]] || compose pull
  compose up --detach --no-build --remove-orphans
  wait_endpoint Auth "http://127.0.0.1:$(env_value AUTH_PORT)/health/ready"
  wait_endpoint Allocator "http://127.0.0.1:$(env_value ALLOCATOR_PORT)/health/ready"
  wait_endpoint Lobby "http://127.0.0.1:$(env_value LOBBY_PORT)/health/ready"
  "$PROJECT_ROOT/Scripts/Linux/smoke-deployment.sh" "$ENV_FILE"
  printf '%s\n' "$target" >"$SCRIPT_DIR/.deployed-version"
  [[ -z "$current" || "$current" == "$target" ]] || printf '%s\n' "$current" >"$SCRIPT_DIR/.previous-version"
  chmod 0600 "$SCRIPT_DIR/.deployed-version" "$SCRIPT_DIR/.previous-version" 2>/dev/null || true
  echo "ROLLBACK_OK version=$target"
}

restore() {
  [[ "$CONFIRM" == true ]] || { echo "restore requires --confirm" >&2; exit 2; }
  [[ -n "$BACKUP_FILE" && -f "$BACKUP_FILE" ]] || { echo "A valid --backup-file is required." >&2; exit 2; }
  ensure_tools
  ensure_environment
  validate_environment
  prepare_data_directories
  local data_root temporary
  data_root="$(env_value MAHJONG_DATA_ROOT)"
  temporary="$(mktemp -d)"
  trap 'rm -rf "$temporary"' RETURN
  tar -xzf "$BACKUP_FILE" -C "$temporary"
  [[ -f "$temporary/postgres.dump" && -f "$temporary/allocator.tar.gz" ]] \
    || { echo "Backup archive is incomplete." >&2; exit 1; }
  compose stop lobby game-node auth
  compose exec -T postgres dropdb -U mahjong --if-exists mahjong
  compose exec -T postgres createdb -U mahjong mahjong
  compose exec -T postgres pg_restore -U mahjong -d mahjong --clean --if-exists <"$temporary/postgres.dump"
  local normalized_root allocator_directory
  normalized_root="$(realpath -m "$data_root")"
  allocator_directory="$(realpath -m "$data_root/allocator")"
  [[ "$normalized_root" != "/" && "$allocator_directory" == "$normalized_root/allocator" ]] \
    || { echo "Refusing unsafe allocator restore target: $allocator_directory" >&2; exit 1; }
  rm -rf --one-file-system -- "$allocator_directory"
  tar -xzf "$temporary/allocator.tar.gz" -C "$data_root"
  chown -R 1654:1654 "$data_root/allocator"
  compose up --detach
  status
  "$PROJECT_ROOT/Scripts/Linux/smoke-deployment.sh" "$ENV_FILE"
  rm -rf "$temporary"
  trap - RETURN
  echo "RESTORE_OK"
}

on_exit() {
  local exit_code=$?
  if ((exit_code != 0)); then
    collect_diagnostics || true
    if [[ "$DEPLOY_STARTED" == true && -n "$ACTIVE_VERSION_BEFORE" ]]; then
      echo "Deployment failed; restoring application version $ACTIVE_VERSION_BEFORE." >&2
      trap - EXIT
      VERSION="$ACTIVE_VERSION_BEFORE"
      if rollback; then
        echo "AUTO_ROLLBACK_OK version=$ACTIVE_VERSION_BEFORE" >&2
      else
        echo "Automatic rollback failed; inspect the diagnostics path above." >&2
      fi
    fi
  fi
  exit "$exit_code"
}
trap on_exit EXIT

case "$ACTION" in
  install|upgrade) deploy ;;
  rollback) rollback ;;
  status) status ;;
  doctor) doctor ;;
  backup) backup ;;
  restore) restore ;;
esac
