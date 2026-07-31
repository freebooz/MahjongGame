FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# 中央 SDK、包版本和 Schema 输出规则必须先进入还原层，保证宿主机与容器构建一致。
COPY Services/Directory.Build.props Services/Directory.Build.targets Services/Directory.Packages.props Services/
COPY Services/Schema/ Services/Schema/
COPY Services/Apps/GuiyangMahjong.Configuration/GuiyangMahjong.Configuration.csproj Services/Apps/GuiyangMahjong.Configuration/
COPY Services/GuiyangMahjong.Observability/GuiyangMahjong.Observability.csproj Services/GuiyangMahjong.Observability/
COPY Services/Contracts/GuiyangMahjong.Contracts.Common/GuiyangMahjong.Contracts.Common.csproj Services/Contracts/GuiyangMahjong.Contracts.Common/
COPY Services/Contracts/GuiyangMahjong.Contracts.Events/GuiyangMahjong.Contracts.Events.csproj Services/Contracts/GuiyangMahjong.Contracts.Events/
COPY Services/BuildingBlocks/GuiyangMahjong.BuildingBlocks.Messaging/GuiyangMahjong.BuildingBlocks.Messaging.csproj Services/BuildingBlocks/GuiyangMahjong.BuildingBlocks.Messaging/
RUN dotnet restore Services/Apps/GuiyangMahjong.Configuration/GuiyangMahjong.Configuration.csproj
COPY Services/GuiyangMahjong.Observability/ Services/GuiyangMahjong.Observability/
COPY Services/Contracts/ Services/Contracts/
COPY Services/BuildingBlocks/GuiyangMahjong.BuildingBlocks.Messaging/ Services/BuildingBlocks/GuiyangMahjong.BuildingBlocks.Messaging/
COPY Services/Apps/GuiyangMahjong.Configuration/ Services/Apps/GuiyangMahjong.Configuration/
RUN dotnet publish Services/Apps/GuiyangMahjong.Configuration/GuiyangMahjong.Configuration.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG INSTALL_HEALTH_TOOLS=true
RUN if [ "$INSTALL_HEALTH_TOOLS" = "true" ]; then apt-get update && apt-get install --yes --no-install-recommends curl && rm -rf /var/lib/apt/lists/*; fi
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "GuiyangMahjong.Configuration.dll"]
