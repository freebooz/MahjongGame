FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# 集中编译策略与包版本必须先进入还原层，否则容器构建会绕过仓库基线。
COPY Services/Directory.Build.props Services/Directory.Packages.props Services/
COPY Services/GuiyangMahjong.Allocator/GuiyangMahjong.Allocator.csproj Services/GuiyangMahjong.Allocator/
COPY Services/GuiyangMahjong.Observability/GuiyangMahjong.Observability.csproj Services/GuiyangMahjong.Observability/
RUN dotnet restore Services/GuiyangMahjong.Allocator/GuiyangMahjong.Allocator.csproj
COPY Contracts/ Contracts/
COPY Services/GuiyangMahjong.Observability/ Services/GuiyangMahjong.Observability/
COPY Services/GuiyangMahjong.Allocator/ Services/GuiyangMahjong.Allocator/
RUN dotnet publish Services/GuiyangMahjong.Allocator/GuiyangMahjong.Allocator.csproj \
      --configuration Release --output /output/allocator --no-restore

FROM build AS server-artifact
COPY Artifacts/LinuxServer/ /input/linux-server/
RUN set -eux; \
    mkdir -p /output/game-server; \
    cp -a /input/linux-server/. /output/game-server/; \
    server_binary="$(find /output/game-server -type f -path '*/Binaries/Linux/GuiyangMahjongServer' -print -quit)"; \
    test -n "$server_binary"; \
    chmod 0755 "$server_binary"; \
    ln -s "${server_binary#/output/game-server/}" /output/game-server/GuiyangMahjongServer; \
    chmod 0755 /output/game-server/GuiyangMahjongServer

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN sed -i 's|http://|https://|g' /etc/apt/sources.list.d/ubuntu.sources \
    && apt-get -o Acquire::Retries=5 update \
    && apt-get install --yes --no-install-recommends curl tini util-linux \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /output/allocator/ ./
COPY --from=server-artifact --chown=$APP_UID:$APP_UID /output/game-server/ /opt/guiyang-mahjong/server/
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080 19000-19099/udp
USER $APP_UID
ENTRYPOINT ["/usr/bin/tini", "--", "dotnet", "GuiyangMahjong.Allocator.dll"]
