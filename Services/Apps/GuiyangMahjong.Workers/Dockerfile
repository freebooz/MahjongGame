FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# 先复制中央构建与项目声明，充分复用 NuGet 还原缓存并保持宿主/容器版本一致。
COPY Services/Directory.Build.props Services/Directory.Build.targets Services/Directory.Packages.props Services/
COPY Services/Schema/ Services/Schema/
COPY Services/Apps/GuiyangMahjong.Workers/GuiyangMahjong.Workers.csproj Services/Apps/GuiyangMahjong.Workers/
COPY Services/GuiyangMahjong.Observability/GuiyangMahjong.Observability.csproj Services/GuiyangMahjong.Observability/
COPY Services/Contracts/GuiyangMahjong.Contracts.Common/GuiyangMahjong.Contracts.Common.csproj Services/Contracts/GuiyangMahjong.Contracts.Common/
COPY Services/Contracts/GuiyangMahjong.Contracts.Events/GuiyangMahjong.Contracts.Events.csproj Services/Contracts/GuiyangMahjong.Contracts.Events/
COPY Services/BuildingBlocks/GuiyangMahjong.BuildingBlocks.Messaging/GuiyangMahjong.BuildingBlocks.Messaging.csproj Services/BuildingBlocks/GuiyangMahjong.BuildingBlocks.Messaging/
COPY Services/BuildingBlocks/GuiyangMahjong.BuildingBlocks.Persistence/GuiyangMahjong.BuildingBlocks.Persistence.csproj Services/BuildingBlocks/GuiyangMahjong.BuildingBlocks.Persistence/
# Persistence 的传递项目引用也必须在 restore 前存在，否则容器构建只会在 publish 阶段才暴露缺失 assets。
COPY Services/BuildingBlocks/GuiyangMahjong.BuildingBlocks.Domain/GuiyangMahjong.BuildingBlocks.Domain.csproj Services/BuildingBlocks/GuiyangMahjong.BuildingBlocks.Domain/
COPY Services/BuildingBlocks/GuiyangMahjong.BuildingBlocks.Idempotency/GuiyangMahjong.BuildingBlocks.Idempotency.csproj Services/BuildingBlocks/GuiyangMahjong.BuildingBlocks.Idempotency/
RUN dotnet restore Services/Apps/GuiyangMahjong.Workers/GuiyangMahjong.Workers.csproj
COPY Services/GuiyangMahjong.Observability/ Services/GuiyangMahjong.Observability/
COPY Services/Contracts/ Services/Contracts/
COPY Services/BuildingBlocks/ Services/BuildingBlocks/
COPY Services/Apps/GuiyangMahjong.Workers/ Services/Apps/GuiyangMahjong.Workers/
RUN dotnet publish Services/Apps/GuiyangMahjong.Workers/GuiyangMahjong.Workers.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG INSTALL_HEALTH_TOOLS=true
RUN if [ "$INSTALL_HEALTH_TOOLS" = "true" ]; then \
      apt-get -o Acquire::Retries=5 update; \
      apt-get install --yes --no-install-recommends curl; \
      rm -rf /var/lib/apt/lists/*; \
    fi
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "GuiyangMahjong.Workers.dll"]
