#!/usr/bin/env bash
# Entrypoint of the KiskEmu image (deploy/docker/README.md). Runs as root only to fix the ownership of mounted folders,
# then drops to the aion user for everything else.
#   db      prepares the PostgreSQL cluster (db_prepare.py) and runs postgres in the foreground
#   aion    writes the host settings (tools/server_env.py) and runs Aion.Host
#   backup  a backup (realm + world databases) every AION_BACKUP_INTERVAL_HOURS (deploy/docker/backup.sh), newest AION_BACKUP_KEEP kept
#   net     holds the network namespace when ZeroTier/Hamachi runs on the host instead (docker-compose.host-vpn.yml)
#   tools   runs a command as aion, e.g. postgres_dev.py role (README "Maintenance")
set -euo pipefail

MODE="${1:-aion}"
[[ $# -gt 0 ]] && shift
STATE=/var/lib/aion
DATA=$STATE/data
export AION_PG_HOME="${AION_PG_HOME:-$STATE/postgres}" AION_BACKUP_DIR="${AION_BACKUP_DIR:-$STATE/backups}"
PG_PORT=55432
READY=/tmp/aion-db-ready

as_aion() { setpriv --reuid=aion --regid=aion --init-groups "$@"; }
log() { echo "[kiskemu $MODE] $*"; }

# Mounted folders arrive owned by whoever made them (a bind mount: the host user). Only the folders themselves and the
# cluster's top level are fixed; nothing is deleted or rewritten.
fix_owner() {
  for dir in "$STATE" "$AION_PG_HOME" "$AION_BACKUP_DIR" "$DATA" "$STATE/ncguard" "$STATE/downloads"; do
    mkdir -p "$dir" 2>/dev/null || true
    chown aion:aion "$dir" 2>/dev/null || log "warning: cannot chown $dir (a Windows bind mount?); it must be writable by uid 10001"
  done
  if [[ -d $AION_PG_HOME/data ]]; then
    chown aion:aion "$AION_PG_HOME/data" "$AION_PG_HOME"/*.json 2>/dev/null || true
    chmod 700 "$AION_PG_HOME/data"
  fi
}

# The server data bundle (pack_server_data.py): unpacked once from ./data/aion-server-data.tar.gz into the data volume.
unpack_data() {
  [[ -f $DATA/bootstrap/keys/app-key.json ]] && return
  local archive
  archive="$(ls /import/*.tar.gz 2>/dev/null | head -1 || true)"
  [[ -n "$archive" ]] || { log "No server data: put aion-server-data.tar.gz in ./data next to docker-compose.yml"; exit 1; }
  log "Unpacking $archive (2-3 GB, a few minutes)"
  as_aion tar -xzf "$archive" -C "$DATA" --no-same-owner
  [[ -f $DATA/bootstrap/keys/app-key.json ]] || { log "$archive holds no bootstrap/keys/app-key.json"; exit 1; }
}

# The address players connect to: AION_PUBLIC_ADDRESS, else this namespace's ZeroTier (zt*) IPv4 once the network
# controller has authorised the node.
public_address() {
  if [[ -n "${AION_PUBLIC_ADDRESS:-}" ]]; then echo "$AION_PUBLIC_ADDRESS"; return; fi
  local wait="${AION_ADDRESS_WAIT:-600}" address=""
  for ((i = 0; i < wait; i += 5)); do
    address="$(ip -4 -o addr show 2>/dev/null | awk '$2 ~ /^zt/ { sub(/\/.*/, "", $4); print $4; exit }')"
    [[ -n "$address" ]] && { echo "$address"; return; }
    (( i % 60 == 0 )) && log "Waiting for a ZeroTier address: authorise this node in your network (my.zerotier.com, Members)" >&2
    sleep 5
  done
  log "No ZeroTier address after ${wait}s; set AION_PUBLIC_ADDRESS or check the zerotier container's log" >&2
  exit 1
}

case "$MODE" in
  db)
    rm -f "$READY"
    fix_owner
    unpack_data
    rc=0
    as_aion python3 /opt/aion/bin/db_prepare.py || rc=$?
    if [[ $rc -eq 3 ]]; then
      # A volume it must not touch: stay down (unhealthy, so aion never starts) instead of retrying in a restart loop.
      log "Refused to use the database volume (see above). Nothing was changed. Fix it, then: docker compose restart db"
      exec sleep infinity
    fi
    [[ $rc -eq 0 ]] || exit $rc
    touch "$READY"
    log "Starting PostgreSQL $PG_MAJOR (127.0.0.1:$PG_PORT, not reachable from the network)"
    exec setpriv --reuid=aion --regid=aion --init-groups "$AION_PG_BIN/postgres" -D "$AION_PG_HOME/data"
    ;;
  aion)
    fix_owner
    ADDRESS="$(public_address)"
    log "Players connect to $ADDRESS"
    # The account site is HTTPS only: KiskLauncher refuses http:// addresses and tools/server_env.py needs a certificate.
    # Put your own cert.pem and key.pem in the state volume (tls/), or a self-signed one is made for the address.
    TLS_DIR=$STATE/tls
    if [[ ! -f $TLS_DIR/cert.pem ]]; then
      log "No $TLS_DIR/cert.pem: making a self-signed certificate for $ADDRESS"
      as_aion mkdir -p "$TLS_DIR"
      as_aion openssl req -x509 -newkey rsa:2048 -nodes -days 825 -subj "/CN=$ADDRESS"         -addext "subjectAltName=IP:$ADDRESS" -keyout "$TLS_DIR/key.pem" -out "$TLS_DIR/cert.pem" 2>/dev/null
    fi
    TLS_ARGS=(--certificate "$TLS_DIR/cert.pem" --key "$TLS_DIR/key.pem")
    # A self-signed certificate is pinned in the launcher.json of the download ZIP (Aion:Launcher:CertificateThumbprint),
    # so the launcher trusts it without a Windows certificate prompt.
    if [[ "$(openssl x509 -in "$TLS_DIR/cert.pem" -noout -subject | sed 's/^subject= *//')" == "$(openssl x509 -in "$TLS_DIR/cert.pem" -noout -issuer | sed 's/^issuer= *//')" ]]; then
      export Aion__Launcher__CertificateThumbprint="$(openssl x509 -in "$TLS_DIR/cert.pem" -noout -fingerprint -sha1 | cut -d= -f2)"
      log "Self-signed certificate; SHA-1 thumbprint for players: $Aion__Launcher__CertificateThumbprint"
    fi
    # The player launcher built into the image (Dockerfile, launcher stage) goes to the downloads folder, so signed-in
    # players download it from the account page (as a ZIP with this server's launcher.json); replaced only when this
    # image's build differs (the launcher's self-update compares hashes).
    LAUNCHER="/opt/aion/launcher/KiskLauncher.exe"
    if [[ -f "$LAUNCHER" ]]; then
      if ! cmp -s "$LAUNCHER" "$STATE/downloads/KiskLauncher.exe"; then
        install -o aion -g aion -m 644 "$LAUNCHER" "$STATE/downloads/KiskLauncher.exe"
        [[ -f "$LAUNCHER.version" ]] && install -o aion -g aion -m 644 "$LAUNCHER.version" "$STATE/downloads/KiskLauncher.exe.version"
        log "Launcher $(cat "$LAUNCHER.version" 2>/dev/null || echo build) ready for download on the account page"
      fi
    else
      log "No launcher in this image (BUILD_LAUNCHER=0); the account page offers none unless you copy one to $STATE/downloads"
    fi
    # Keep the staff dashboard's private address across restarts.
    STAFF_PATH="$(grep -s '^Aion__Web__StaffPath=' $STATE/aion.env | cut -d= -f2- || true)"
    [[ -n "$STAFF_PATH" ]] && TLS_ARGS+=(--staff-path "$STAFF_PATH")
    # Extra server_env.py options (rates, player cap, ...): AION_SERVER_ARGS="--max-players 10 --rates-profile friends".
    read -r -a EXTRA <<< "${AION_SERVER_ARGS:-}"
    as_aion python3 /opt/aion/src/tools/server_env.py --public-address "$ADDRESS" --bind 0.0.0.0 --data "$DATA" --state "$STATE" \
      "${TLS_ARGS[@]}" "${EXTRA[@]}" > $STATE/aion.env.new
    chown aion:aion $STATE/aion.env.new && chmod 600 $STATE/aion.env.new && mv $STATE/aion.env.new $STATE/aion.env
    while IFS= read -r line; do [[ "$line" =~ ^[A-Za-z_][A-Za-z0-9_]*= ]] && export "$line"; done < $STATE/aion.env
    log "Account site: https://$ADDRESS:8780 ; staff dashboard: $Aion__Web__StaffPath"
    cd /opt/aion/host
    # Port 443 (NCGuard) is below 1024: the aion user keeps only the capability to bind it.
    exec setpriv --reuid=aion --regid=aion --init-groups --inh-caps=+net_bind_service --ambient-caps=+net_bind_service \
      dotnet /opt/aion/host/Aion.Host.dll
    ;;
  backup)
    fix_owner
    HOURS="${AION_BACKUP_INTERVAL_HOURS:-24}"
    [[ "$HOURS" =~ ^[1-9][0-9]{0,3}$ ]] || { log "AION_BACKUP_INTERVAL_HOURS must be a whole number of hours"; exit 2; }
    while true; do
      as_aion env AION_RUN_AS= /opt/aion/bin/backup.sh || log "Backup failed; the earlier backups are kept"
      sleep $((HOURS * 3600))
    done
    ;;
  net)
    exec sleep infinity
    ;;
  tools)
    fix_owner
    exec setpriv --reuid=aion --regid=aion --init-groups env AION_RUN_AS= "$@"
    ;;
  *)
    echo "Unknown mode $MODE (db, aion, backup, net, tools)" >&2; exit 2 ;;
esac
