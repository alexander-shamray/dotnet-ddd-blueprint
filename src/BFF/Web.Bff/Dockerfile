# syntax=docker/dockerfile:1

# The tag names the exact patch global.json pins (§4.4), so a bump there is a
# bump here, in the same change.
FROM mcr.microsoft.com/dotnet/sdk:10.0.302-noble AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Project files first, so the restore layer really does survive source-only
# changes.
#
# **Every project in the transitive closure gets a line, and a missing one
# fails four steps later rather than here.** dotnet restore writes each
# project's own obj/project.assets.json; a csproj absent when it runs is not
# restored, and the --no-restore publish then fails with NETSDK1004 naming a
# project this file never mentions. Nothing in CI catches it: the compose smoke
# is the only job that builds these images and it is path-filtered on
# deploy/compose/**, while a new ProjectReference lands under src/.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/BuildingBlocks/Common.Domain/Common.Domain.csproj src/BuildingBlocks/Common.Domain/
COPY src/BuildingBlocks/Common.Application/Common.Application.csproj src/BuildingBlocks/Common.Application/
COPY src/BuildingBlocks/Common.Web/Common.Web.csproj src/BuildingBlocks/Common.Web/
COPY src/BFF/Web.Bff/Web.Bff.csproj src/BFF/Web.Bff/
RUN dotnet restore src/BFF/Web.Bff/Web.Bff.csproj

# .editorconfig rides with the source, not the restore inputs: it is a build
# input under ADR-019 (EnforceCodeStyleInBuild reads it), and without it this
# publish would enforce a weaker style policy than every other build.
COPY .editorconfig ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/BFF/ src/BFF/

# THE ONE LINE THAT IS NOT LIKE THE OTHER IMAGES, and it is the price of the
# linked .proto (§4.3, and this project's csproj). Web.Bff compiles Catalog's
# contract by relative path, so the file has to be in the build context even
# though no Catalog project is — and it is a *source* input rather than a
# restore one, which is why it sits here and not above.
#
# The failure mode is loud and its message is misleading: without this,
# Grpc.Tools reports the .proto missing and names a path under
# src/Services/Catalog that the reader will go looking for in a Dockerfile that
# builds no Catalog project at all. A Catalog *rename* breaks it the same way.
COPY src/Services/Catalog/Catalog.Api/Protos/ src/Services/Catalog/Catalog.Api/Protos/

RUN dotnet publish src/BFF/Web.Bff/Web.Bff.csproj \
    -c $BUILD_CONFIGURATION -o /app/publish --no-restore /p:UseAppHost=false

# Plain chiselled, NOT -extra, and the difference from every other image here
# is deliberate. -extra buys ICU and tzdata, which the services need because
# Microsoft.Data.SqlClient refuses to open a connection under
# globalization-invariant mode. This host owns no database and opens no SQL
# connection (§10.1), so it pays for neither.
#
# What globalization-invariant mode WOULD break is culture-sensitive parsing,
# and this host does one piece of it — the price decimal off the wire. That
# parse names InvariantCulture explicitly at both ends (pricing.proto), which
# is exactly the property that makes it safe here: invariant mode is the
# invariant culture, so the one number this image handles is unaffected.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app/publish .

# Chiselled images ship no shell and run as a non-root user by default.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Web.Bff.dll"]
