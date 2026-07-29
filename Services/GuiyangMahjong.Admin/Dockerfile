FROM node:24-alpine AS client-build
WORKDIR /src/ClientApp
# 先复制锁文件以复用依赖层，再执行 Angular 22 的类型检查和生产构建。
COPY Services/GuiyangMahjong.Admin/ClientApp/package.json Services/GuiyangMahjong.Admin/ClientApp/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY Services/GuiyangMahjong.Admin/ClientApp/ ./
RUN npm run typecheck && npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Services/GuiyangMahjong.Admin/GuiyangMahjong.Admin.csproj Services/GuiyangMahjong.Admin/
COPY Services/GuiyangMahjong.Observability/GuiyangMahjong.Observability.csproj Services/GuiyangMahjong.Observability/
RUN dotnet restore Services/GuiyangMahjong.Admin/GuiyangMahjong.Admin.csproj
COPY Services/GuiyangMahjong.Observability/ Services/GuiyangMahjong.Observability/
COPY Services/GuiyangMahjong.Admin/ Services/GuiyangMahjong.Admin/
# 使用刚生成的前端产物，避免 SDK 镜像再安装 Node.js。
COPY --from=client-build /src/wwwroot/ Services/GuiyangMahjong.Admin/wwwroot/
RUN dotnet publish Services/GuiyangMahjong.Admin/GuiyangMahjong.Admin.csproj -c Release -o /app --no-restore -p:SkipAdminClientBuild=true

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
ENTRYPOINT ["dotnet", "GuiyangMahjong.Admin.dll"]
