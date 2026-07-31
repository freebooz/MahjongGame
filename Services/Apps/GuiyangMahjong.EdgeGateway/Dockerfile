FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# 先复制中央构建与包版本文件，确保容器和宿主机使用同一 .NET 10/YARP 依赖基线。
COPY Services/Directory.Build.props Services/Directory.Build.targets Services/Directory.Packages.props Services/
COPY Services/Apps/GuiyangMahjong.EdgeGateway/GuiyangMahjong.EdgeGateway.csproj Services/Apps/GuiyangMahjong.EdgeGateway/
COPY Services/GuiyangMahjong.Observability/GuiyangMahjong.Observability.csproj Services/GuiyangMahjong.Observability/
RUN dotnet restore Services/Apps/GuiyangMahjong.EdgeGateway/GuiyangMahjong.EdgeGateway.csproj
COPY Services/GuiyangMahjong.Observability/ Services/GuiyangMahjong.Observability/
COPY Services/Apps/GuiyangMahjong.EdgeGateway/ Services/Apps/GuiyangMahjong.EdgeGateway/
RUN dotnet publish Services/Apps/GuiyangMahjong.EdgeGateway/GuiyangMahjong.EdgeGateway.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG INSTALL_HEALTH_TOOLS=true
# curl 只用于容器内健康探针；运行阶段仍使用无特权 APP_UID。
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
ENTRYPOINT ["dotnet", "GuiyangMahjong.EdgeGateway.dll"]
