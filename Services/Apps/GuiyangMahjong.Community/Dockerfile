FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish Services/Apps/GuiyangMahjong.Community/GuiyangMahjong.Community.csproj -c Release -o /out --no-self-contained
FROM runtime
COPY --from=build /out .
USER $APP_UID
ENTRYPOINT ["dotnet", "GuiyangMahjong.Community.dll"]
