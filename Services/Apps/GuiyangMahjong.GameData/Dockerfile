FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# 中央构建、包版本和 Schema 路径规则必须先进入还原层，保证容器与宿主机构建一致。
COPY Services/Directory.Build.props Services/Directory.Build.targets Services/Directory.Packages.props Services/
COPY Services/Schema/ Services/Schema/
COPY Services/Apps/GuiyangMahjong.GameData/GuiyangMahjong.GameData.csproj Services/Apps/GuiyangMahjong.GameData/
COPY Services/GuiyangMahjong.Observability/GuiyangMahjong.Observability.csproj Services/GuiyangMahjong.Observability/
COPY Services/Contracts/GuiyangMahjong.Contracts.Common/GuiyangMahjong.Contracts.Common.csproj Services/Contracts/GuiyangMahjong.Contracts.Common/
COPY Services/Contracts/GuiyangMahjong.Contracts.Events/GuiyangMahjong.Contracts.Events.csproj Services/Contracts/GuiyangMahjong.Contracts.Events/
RUN dotnet restore Services/Apps/GuiyangMahjong.GameData/GuiyangMahjong.GameData.csproj
COPY Services/GuiyangMahjong.Observability/ Services/GuiyangMahjong.Observability/
COPY Services/Contracts/ Services/Contracts/
COPY Services/Apps/GuiyangMahjong.GameData/ Services/Apps/GuiyangMahjong.GameData/
RUN dotnet publish Services/Apps/GuiyangMahjong.GameData/GuiyangMahjong.GameData.csproj -c Release -o /app --no-restore

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
ENTRYPOINT ["dotnet", "GuiyangMahjong.GameData.dll"]
