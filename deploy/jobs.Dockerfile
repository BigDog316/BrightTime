# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

# The whole of src/ is copied before the restore, so any source change reruns it.
COPY global.json Directory.Build.props Directory.Packages.props NuGet.config .editorconfig .globalconfig ./
COPY src/ src/
COPY tests/Directory.Build.props tests/
RUN dotnet restore src/BrightTime.Jobs/BrightTime.Jobs.csproj

RUN dotnet publish src/BrightTime.Jobs/BrightTime.Jobs.csproj \
    --no-restore -c Release -o /app

# The modules reference the ASP.NET Core shared framework, so this needs the aspnet runtime.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app

# Non-root. The container writes nothing outside /tmp.
RUN useradd --uid 64198 --create-home --shell /usr/sbin/nologin brighttime
USER 64198

COPY --from=build --chown=64198:64198 /app .

ENTRYPOINT ["dotnet", "BrightTime.Jobs.dll"]
