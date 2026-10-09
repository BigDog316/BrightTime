# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

# Restore against the manifest files alone so a source-only change does not
# invalidate the package layer.
COPY global.json Directory.Build.props Directory.Packages.props NuGet.config .editorconfig .globalconfig ./
COPY src/ src/
COPY tests/Directory.Build.props tests/
RUN dotnet restore src/BrightTime.Web/BrightTime.Web.csproj

RUN dotnet publish src/BrightTime.Web/BrightTime.Web.csproj \
    --no-restore -c Release -o /app \
    -p:UseArtifactsOutput=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app

# Non-root. The container writes nothing outside /tmp.
RUN useradd --uid 64198 --create-home --shell /usr/sbin/nologin brighttime
USER 64198

COPY --from=build --chown=64198:64198 /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "BrightTime.Web.dll"]
