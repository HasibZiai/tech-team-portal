# Tech Team Portal: container image
#
# You usually won't use this file directly:
#   Try it on any PC:    docker compose up -d          (compose.yaml, http://localhost:8080)
#   Production VM:       see deploy/docker-compose.yml
#
# The ":10.0" tags follow .NET 10 LTS (supported until November 2028) and pick up security
# patches whenever the image is rebuilt. Moving to a new .NET version is a deliberate change.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so package downloads are cached between builds.
COPY global.json ./
COPY src/PcServ.Core/PcServ.Core.csproj src/PcServ.Core/
COPY src/PcServ.Data/PcServ.Data.csproj src/PcServ.Data/
COPY src/PcServ.Web/PcServ.Web.csproj src/PcServ.Web/
RUN dotnet restore src/PcServ.Web/PcServ.Web.csproj

COPY src/ src/
RUN dotnet publish src/PcServ.Web/PcServ.Web.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Connects the published image to its repository on GitHub.
LABEL org.opencontainers.image.source="https://github.com/HasibZiai/tech-team-portal"

# All saved data (database, sign-in keys, future uploads) lives here. Mount a volume on it.
# The folder must belong to the non-root app user so that a new volume inherits write access.
RUN mkdir -p /app/data && chown -R "$APP_UID" /app/data
VOLUME /app/data

ENV ASPNETCORE_HTTP_PORTS=8080 \
    PcServ__DataDirectory=/app/data \
    PcServ__BehindProxy=true

COPY --from=build /app/publish .

USER $APP_UID
EXPOSE 8080

# The image has no curl, so the app checks itself (see Services/HealthCheck.cs).
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD ["dotnet", "PcServ.Web.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "PcServ.Web.dll"]
