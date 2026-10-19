# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props NuGet.config .editorconfig .globalconfig ./
COPY src/ src/
COPY tests/Directory.Build.props tests/
RUN dotnet restore src/BrightTime.Seed/BrightTime.Seed.csproj

RUN dotnet publish src/BrightTime.Seed/BrightTime.Seed.csproj \
    --no-restore -c Release -o /app

# The modules reference the ASP.NET Core shared framework, so this needs the aspnet runtime.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app

# Non-root. A one-shot job: it loads the Kettle County tenant and exits.
RUN useradd --uid 64198 --create-home --shell /usr/sbin/nologin brighttime
USER 64198

COPY --from=build --chown=64198:64198 /app .

ENTRYPOINT ["dotnet", "BrightTime.Seed.dll"]
