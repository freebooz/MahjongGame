#!/usr/bin/env bash
# 验证 Linux 服务镜像构建、容器启动、健康探针和最小跨服务调用契约。
# 依赖缺失或健康超时必须失败，测试环境变量不得复用生产凭据。
set -Eeuo pipefail

PROJECT_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
ENV_FILE="${1:-$PROJECT_ROOT/Deploy/linux/.env}"
NETWORK="${2:-guiyang-mahjong_default}"

[[ -f "$ENV_FILE" ]] || { echo "Environment file not found: $ENV_FILE" >&2; exit 2; }

env_value() {
  local key="$1"
  awk -F= -v key="$key" '$1 == key {sub(/^[^=]*=/, ""); print; exit}' "$ENV_FILE"
}

postgres_password="$(env_value POSTGRES_PASSWORD)"
[[ -n "$postgres_password" ]] || { echo "POSTGRES_PASSWORD is missing." >&2; exit 1; }

common=(--rm
  -v guiyang-nuget:/root/.nuget/packages
  -v guiyang-test-artifacts:/artifacts
  -v "$PROJECT_ROOT/Services:/src:ro"
  -v "$PROJECT_ROOT/Contracts:/Contracts:ro"
  -w /src
  mcr.microsoft.com/dotnet/sdk:10.0)

docker run --network host \
  -e HTTP_PROXY="${HTTP_PROXY:-${http_proxy:-}}" \
  -e HTTPS_PROXY="${HTTPS_PROXY:-${https_proxy:-}}" \
  -e NO_PROXY=127.0.0.1,localhost \
  "${common[@]}" \
  dotnet test GuiyangMahjong.Services.slnx --configuration Release \
    --artifacts-path /artifacts \
    --filter 'Category!=ExternalPersistence' --logger 'console;verbosity=minimal'

docker run --network "$NETWORK" \
  -e "AUTH_TEST_POSTGRES=Host=postgres;Port=5432;Database=mahjong;Username=mahjong;Password=$postgres_password" \
  -e "LOBBY_TEST_POSTGRES=Host=postgres;Port=5432;Database=mahjong;Username=mahjong;Password=$postgres_password" \
  -e LOBBY_TEST_REDIS=redis:6379,abortConnect=false \
  "${common[@]}" \
  dotnet test GuiyangMahjong.Services.slnx --configuration Release --no-build \
    --artifacts-path /artifacts \
    --filter 'Category=ExternalPersistence' --logger 'console;verbosity=minimal'

echo "LINUX_SERVICES_TESTS_OK"
