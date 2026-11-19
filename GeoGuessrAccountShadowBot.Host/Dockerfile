# Base images are pinned by digest for reproducible, tamper-evident builds. Dependabot raises pull
# requests to bump them.
# The ASP.NET Core runtime, because the bot can serve an API to other instances.
FROM mcr.microsoft.com/dotnet/aspnet:11.0@sha256:bc0d6653979efba491ab3d6f589d691f96c309c3c3ef81325efa6978e7b7f242 AS base
# Created before switching user, so that a named volume mounted here inherits this ownership.
RUN install -d -o 1654 -g 1654 -m 0750 /data
USER 1654
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:11.0@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["global.json", "Directory.Build.props", "Directory.Packages.props", "./"]
COPY ["GeoGuessrAccountShadowBot.Domain/GeoGuessrAccountShadowBot.Domain.csproj", "GeoGuessrAccountShadowBot.Domain/"]
COPY ["GeoGuessrAccountShadowBot.Application/GeoGuessrAccountShadowBot.Application.csproj", "GeoGuessrAccountShadowBot.Application/"]
COPY ["GeoGuessrAccountShadowBot.Infrastructure/GeoGuessrAccountShadowBot.Infrastructure.csproj", "GeoGuessrAccountShadowBot.Infrastructure/"]
COPY ["GeoGuessrAccountShadowBot.Discord/GeoGuessrAccountShadowBot.Discord.csproj", "GeoGuessrAccountShadowBot.Discord/"]
COPY ["GeoGuessrAccountShadowBot.Api/GeoGuessrAccountShadowBot.Api.csproj", "GeoGuessrAccountShadowBot.Api/"]
COPY ["GeoGuessrAccountShadowBot.Host/GeoGuessrAccountShadowBot.Host.csproj", "GeoGuessrAccountShadowBot.Host/"]
RUN dotnet restore "GeoGuessrAccountShadowBot.Host/GeoGuessrAccountShadowBot.Host.csproj"
COPY . .
RUN dotnet publish "GeoGuessrAccountShadowBot.Host/GeoGuessrAccountShadowBot.Host.csproj" -c $BUILD_CONFIGURATION -o /app/publish --no-restore /p:UseAppHost=false

FROM base AS final
COPY --from=build /app/publish .
# The image is a GPL distribution, so it carries the license.
COPY LICENSE /app/LICENSE
# The bot touches this file while it works, which avoids opening a port just for a health check.
HEALTHCHECK --interval=30s --timeout=3s --start-period=60s CMD ["sh", "-c", "[ $(( $(date +%s) - $(stat -c %Y /tmp/shadow-healthy) )) -lt 90 ]"]
# The external API, which only listens when ExternalApi:Enabled is on.
EXPOSE 8080
ENTRYPOINT ["dotnet", "GeoGuessrAccountShadowBot.dll"]
