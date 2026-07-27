FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Services/GuiyangMahjong.Admin/GuiyangMahjong.Admin.csproj Services/GuiyangMahjong.Admin/
RUN dotnet restore Services/GuiyangMahjong.Admin/GuiyangMahjong.Admin.csproj
COPY Services/GuiyangMahjong.Admin/ Services/GuiyangMahjong.Admin/
RUN dotnet publish Services/GuiyangMahjong.Admin/GuiyangMahjong.Admin.csproj -c Release -o /app --no-restore

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
