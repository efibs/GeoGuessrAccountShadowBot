# Base images are pinned by digest for reproducible, tamper-evident builds. Dependabot raises pull
# requests to bump them.
FROM mcr.microsoft.com/dotnet/runtime:10.0@sha256:a3f9f81954b9c810af2ed18849716c8dc74a4707bd0907fdbd69be81554c0f30 AS base
# Created before switching user, so that a named volume mounted here inherits this ownership.
RUN install -d -o 1654 -g 1654 -m 0750 /data
USER 1654
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["global.json", "Directory.Build.props", "Directory.Packages.props", "./"]
COPY ["GeoGuessrAccountShadowBot.Domain/GeoGuessrAccountShadowBot.Domain.csproj", "GeoGuessrAccountShadowBot.Domain/"]
COPY ["GeoGuessrAccountShadowBot.Domain/packages.lock.json", "GeoGuessrAccountShadowBot.Domain/"]
COPY ["GeoGuessrAccountShadowBot.Application/GeoGuessrAccountShadowBot.Application.csproj", "GeoGuessrAccountShadowBot.Application/"]
COPY ["GeoGuessrAccountShadowBot.Application/packages.lock.json", "GeoGuessrAccountShadowBot.Application/"]
COPY ["GeoGuessrAccountShadowBot.Infrastructure/GeoGuessrAccountShadowBot.Infrastructure.csproj", "GeoGuessrAccountShadowBot.Infrastructure/"]
COPY ["GeoGuessrAccountShadowBot.Infrastructure/packages.lock.json", "GeoGuessrAccountShadowBot.Infrastructure/"]
COPY ["GeoGuessrAccountShadowBot.Discord/GeoGuessrAccountShadowBot.Discord.csproj", "GeoGuessrAccountShadowBot.Discord/"]
COPY ["GeoGuessrAccountShadowBot.Discord/packages.lock.json", "GeoGuessrAccountShadowBot.Discord/"]
COPY ["GeoGuessrAccountShadowBot.Host/GeoGuessrAccountShadowBot.Host.csproj", "GeoGuessrAccountShadowBot.Host/"]
COPY ["GeoGuessrAccountShadowBot.Host/packages.lock.json", "GeoGuessrAccountShadowBot.Host/"]
RUN dotnet restore "GeoGuessrAccountShadowBot.Host/GeoGuessrAccountShadowBot.Host.csproj" --locked-mode
COPY . .
RUN dotnet publish "GeoGuessrAccountShadowBot.Host/GeoGuessrAccountShadowBot.Host.csproj" -c $BUILD_CONFIGURATION -o /app/publish --no-restore /p:UseAppHost=false

FROM base AS final
COPY --from=build /app/publish .
# The image is a GPL distribution, so it carries the license.
COPY LICENSE /app/LICENSE
# The bot touches this file while it works, which avoids opening a port just for a health check.
HEALTHCHECK --interval=30s --timeout=3s --start-period=60s CMD ["sh", "-c", "[ $(( $(date +%s) - $(stat -c %Y /tmp/shadow-healthy) )) -lt 90 ]"]
ENTRYPOINT ["dotnet", "GeoGuessrAccountShadowBot.dll"]
