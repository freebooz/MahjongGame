#!/usr/bin/env bash
set -Eeuo pipefail

# 构建 Auth、Lobby、PlayerData、Admin 和包含真实 Dedicated Server 的 game-node。
# 脚本只写入本机 Docker 镜像缓存，不启动容器、不推送仓库，也不修改部署状态。
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
project_root="$(cd -- "${script_dir}/../.." && pwd)"
tag_suffix="batch2"
compile_only=false
no_cache=false

while (($# > 0)); do
  case "$1" in
    --tag)
      tag_suffix="${2:?--tag requires a value}"
      shift 2
      ;;
    --compile-only)
      compile_only=true
      shift
      ;;
    --no-cache)
      no_cache=true
      shift
      ;;
    *)
      printf 'Unknown argument: %s\n' "$1" >&2
      exit 2
      ;;
  esac
done

if [[ ! "${tag_suffix}" =~ ^[A-Za-z0-9._-]+$ ]]; then
  printf 'Invalid image tag suffix: %s\n' "${tag_suffix}" >&2
  exit 2
fi
if [[ ! -f "${project_root}/Services/Directory.Packages.props" ]]; then
  printf 'Project root validation failed: %s\n' "${project_root}" >&2
  exit 2
fi
docker info >/dev/null

# 每个条目使用“名称|Dockerfile|可选 target”。Allocator 编译矩阵只构建 build stage；
# 本地生产矩阵在存在真实 LinuxServer 产物时继续构建最终 game-node。
entries=(
  "auth|Services/GuiyangMahjong.Auth/Dockerfile|"
  "lobby|Services/GuiyangMahjong.Lobby/Dockerfile|"
  "player-data|Services/GuiyangMahjong.PlayerData/Dockerfile|"
  "admin|Services/GuiyangMahjong.Admin/Dockerfile|"
)
if [[ "${compile_only}" == true ]]; then
  entries+=("allocator-build|Services/GuiyangMahjong.Allocator/Dockerfile|build")
else
  if [[ ! -d "${project_root}/Artifacts/LinuxServer" ]]; then
    printf 'Real LinuxServer artifact directory is required for the game-node image.\n' >&2
    exit 3
  fi
  server_binary="$(
    find "${project_root}/Artifacts/LinuxServer" \
      -type f -path '*/Binaries/Linux/GuiyangMahjongServer' -print -quit 2>/dev/null
  )"
  if [[ -z "${server_binary}" ]]; then
    printf 'Real LinuxServer artifact is required for the game-node image.\n' >&2
    exit 3
  fi
  entries+=("game-node|Services/GuiyangMahjong.Allocator/Dockerfile|")
fi

cd "${project_root}"
for entry in "${entries[@]}"; do
  IFS='|' read -r name dockerfile target <<<"${entry}"
  image="local/guiyang-mahjong-${name}:${tag_suffix}"
  arguments=(
    build
    --file "${dockerfile}"
    --tag "${image}"
    --label "org.opencontainers.image.revision=${GITHUB_SHA:-local}"
  )
  if [[ -n "${target}" ]]; then
    arguments+=(--target "${target}")
  fi
  if [[ "${no_cache}" == true ]]; then
    arguments+=(--no-cache)
  fi
  arguments+=(.)
  printf 'DOCKER_MATRIX_BUILD_START name=%s dockerfile=%s target=%s\n' \
    "${name}" "${dockerfile}" "${target:-final}"
  docker "${arguments[@]}"
  printf 'DOCKER_MATRIX_BUILD_OK name=%s image=%s\n' "${name}" "${image}"
done

printf 'DOCKER_SERVICE_IMAGE_MATRIX_OK count=%s mode=%s tag=%s\n' \
  "${#entries[@]}" \
  "$([[ "${compile_only}" == true ]] && printf compile || printf production)" \
  "${tag_suffix}"
