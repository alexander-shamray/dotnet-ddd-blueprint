# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Restore in its own layer so it caches across source-only changes.
# global.json first among equals: sdk:10.0-noble is a floating tag, so without
# it this build — the only one whose output ships — compiles under whatever SDK
# the base image carries that week, while developers and CI are pinned (§4.4).
# Copied in, a mismatch is a restore error rather than different analysers.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Catalog/ src/Services/Catalog/
RUN dotnet restore src/Services/Catalog/Catalog.Api/Catalog.Api.csproj

RUN dotnet publish src/Services/Catalog/Catalog.Api/Catalog.Api.csproj \
    -c $BUILD_CONFIGURATION -o /app/publish --no-restore /p:UseAppHost=false

# -extra, not plain chiseled: the plain image runs in globalization-invariant
# mode and Microsoft.Data.SqlClient refuses to open a connection under it —
# the migrator found this at first run, and every service here talks to SQL.
# The extra layer is ICU and tzdata, nothing else; still no shell, no root.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS final
WORKDIR /app
COPY --from=build /app/publish .

# Chiselled images ship no shell and run as a non-root user by default.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Catalog.Api.dll"]
