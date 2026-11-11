# KiskEmu server image: the host (Aion.Host), the project's PostgreSQL tooling and the backup scripts.
# One image runs every compose service (db, aion, backup); see deploy/docker/README.md.
# Build from the repository root: docker compose build   (docker-compose.yml sets the context)

# ---- Account page and staff dashboard (React) -> server/Aion.Host/wwwroot ----
FROM node:22.12-bookworm-slim AS web
WORKDIR /src
COPY web/package.json web/package-lock.json web/
RUN cd web && npm ci --no-audit --no-fund
COPY web/ web/
COPY server/Aion.Host/wwwroot/ server/Aion.Host/wwwroot/
RUN cd web && npm run build

# ---- Host ----
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY server/ server/
COPY tools/data/ tools/data/
COPY --from=web /src/server/Aion.Host/wwwroot/ server/Aion.Host/wwwroot/
# The pages are already built (AION_SKIP_WEB=1: no Node.js in this stage).
RUN dotnet publish server/Aion.Host/Aion.Host.csproj -c Release -o /out/host --nologo -p:AION_SKIP_WEB=1

# ---- Player launcher (Windows exe, cross-compiled; launcher/README.md) ----
# The aion service copies it into the downloads folder at every start, so signed-in players can download it from the
# account page. BUILD_LAUNCHER=0 (docker compose build --build-arg BUILD_LAUNCHER=0) skips it.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS launcher
ARG BUILD_LAUNCHER=1
WORKDIR /src
COPY launcher/ launcher/
RUN mkdir -p /out/launcher \
 && if [ "$BUILD_LAUNCHER" = 1 ]; then \
      dotnet publish launcher/AionLauncher -c Release -r win-x64 -o /out/launcher --nologo \
        -p:DebugType=none -p:EnableWindowsTargeting=true \
      && sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' launcher/AionLauncher/AionLauncher.csproj \
        > "/out/launcher/KiskLauncher.exe.version"; \
    fi

# ---- Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
# PostgreSQL is pinned to one major version: a cluster made by 16 only starts under 16. Changing it needs a dump and a
# restore (deploy/docker/README.md, "Upgrading PostgreSQL"); the entrypoint refuses to start a cluster of another version
# instead of touching it.
ARG PG_MAJOR=16
ENV PG_MAJOR=${PG_MAJOR} \
    AION_PG_BIN=/usr/lib/postgresql/${PG_MAJOR}/bin \
    AION_PG_HOME=/var/lib/aion/postgres \
    AION_PG_TOOL=/opt/aion/src/tools/postgres_dev.py \
    AION_RUN_AS= \
    DOTNET_gcServer=0
# No distribution cluster: the project's own cluster lives in the a2core-postgres volume (tools/postgres_dev.py).
RUN mkdir -p /etc/postgresql-common/createcluster.d \
 && echo 'create_main_cluster = false' > /etc/postgresql-common/createcluster.d/a2core.conf \
 && apt-get update \
 && apt-get install -y --no-install-recommends postgresql-${PG_MAJOR} python3 iproute2 util-linux tini openssl ca-certificates \
 && rm -rf /var/lib/apt/lists/* \
 && useradd --system --uid 10001 --user-group --home-dir /var/lib/aion --shell /usr/sbin/nologin aion \
 && install -d -o aion -g aion -m 750 /var/lib/aion /var/lib/aion/postgres /var/lib/aion/backups /var/lib/aion/data \
        /var/lib/aion/ncguard /var/lib/aion/downloads

COPY --from=build /out/host/ /opt/aion/host/
COPY --from=launcher /out/launcher/ /opt/aion/launcher/
COPY tools/ /opt/aion/src/tools/
COPY database/ /opt/aion/src/database/
COPY deploy/docker/entrypoint.sh deploy/docker/db_prepare.py deploy/docker/backup.sh /opt/aion/bin/
RUN chmod 755 /opt/aion/bin/*.sh

WORKDIR /var/lib/aion
ENTRYPOINT ["/usr/bin/tini", "--", "/opt/aion/bin/entrypoint.sh"]
CMD ["aion"]
