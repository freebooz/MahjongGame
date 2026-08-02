FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# Compose 健康检查使用 curl；运行时镜像必须显式提供该诊断工具，避免服务可用却被标记为 unhealthy。
RUN sed -i 's|http://|https://|g' /etc/apt/sources.list.d/ubuntu.sources \
    && apt-get -o Acquire::Retries=5 update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
EXPOSE 8080
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish Services/Apps/GuiyangMahjong.Economy/GuiyangMahjong.Economy.csproj -c Release -o /out --no-self-contained
FROM runtime
COPY --from=build /out .
USER $APP_UID
ENTRYPOINT ["dotnet", "GuiyangMahjong.Economy.dll"]
