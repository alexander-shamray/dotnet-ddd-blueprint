# src/Services/Catalog/Catalog.Migrator/Dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Catalog/ src/Services/Catalog/
# Restore as its own layer, same as the API image — it caches across
# source-only changes, which is most changes.
RUN dotnet restore src/Services/Catalog/Catalog.Migrator/Catalog.Migrator.csproj

RUN dotnet publish src/Services/Catalog/Catalog.Migrator/Catalog.Migrator.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false

# Runtime, not aspnet — the migrator has no listener. -extra, because plain
# chiseled runs globalization-invariant and Microsoft.Data.SqlClient refuses
# it; ICU and tzdata are the whole difference.
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra AS final
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Catalog.Migrator.dll"]
