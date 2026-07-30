FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# 集中编译策略与包版本必须先进入还原层，否则容器构建会绕过仓库基线。
COPY Services/Directory.Build.props Services/Directory.Packages.props Services/
COPY Services/GuiyangMahjong.Lobby/GuiyangMahjong.Lobby.csproj Services/GuiyangMahjong.Lobby/
COPY Services/GuiyangMahjong.Observability/GuiyangMahjong.Observability.csproj Services/GuiyangMahjong.Observability/
RUN dotnet restore Services/GuiyangMahjong.Lobby/GuiyangMahjong.Lobby.csproj
COPY Contracts/ Contracts/
COPY Services/GuiyangMahjong.Observability/ Services/GuiyangMahjong.Observability/
COPY Services/GuiyangMahjong.Lobby/ Services/GuiyangMahjong.Lobby/
RUN dotnet publish Services/GuiyangMahjong.Lobby/GuiyangMahjong.Lobby.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG INSTALL_HEALTH_TOOLS=true
RUN if [ "$INSTALL_HEALTH_TOOLS" = "true" ]; then \
      sed -i 's|http://|https://|g' /etc/apt/sources.list.d/ubuntu.sources; \
      apt-get -o Acquire::Retries=5 update; \
      apt-get install --yes --no-install-recommends curl; \
      rm -rf /var/lib/apt/lists/*; \
    fi
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "GuiyangMahjong.Lobby.dll"]
