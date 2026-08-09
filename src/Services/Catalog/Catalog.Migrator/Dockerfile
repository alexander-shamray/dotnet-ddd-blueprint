# src/Services/Catalog/Catalog.Migrator/Dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

# Project files first, same as the API image and for the same reason: restore
# in a layer that survives source-only changes, which is most changes. No
# Common.Web — the migrator's reference chain stops at Infrastructure.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/BuildingBlocks/Common.Domain/Common.Domain.csproj src/BuildingBlocks/Common.Domain/
COPY src/BuildingBlocks/Common.Application/Common.Application.csproj src/BuildingBlocks/Common.Application/
COPY src/Services/Catalog/Catalog.Domain/Catalog.Domain.csproj src/Services/Catalog/Catalog.Domain/
COPY src/Services/Catalog/Catalog.Application/Catalog.Application.csproj src/Services/Catalog/Catalog.Application/
COPY src/Services/Catalog/Catalog.Infrastructure/Catalog.Infrastructure.csproj src/Services/Catalog/Catalog.Infrastructure/
COPY src/Services/Catalog/Catalog.Migrator/Catalog.Migrator.csproj src/Services/Catalog/Catalog.Migrator/
RUN dotnet restore src/Services/Catalog/Catalog.Migrator/Catalog.Migrator.csproj

# .editorconfig is a build input under ADR-019 — without it this publish
# enforces a weaker style policy than every other build.
COPY .editorconfig ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Catalog/ src/Services/Catalog/
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
