#!/usr/bin/env bash
# One backup of the realm database and every world database (tools/postgres_dev.py backup), verified, with retention.
# Run by the backup service of docker-compose.yml (entrypoint.sh, mode backup) every AION_BACKUP_INTERVAL_HOURS.
#   nightly-<UTC time>.dump              the realm database
#   nightly-<UTC time>.world-<id>.dump   one per world database
# Each file is restored into a disposable database (postgres_dev.py verify-backup). When a file does not verify, all files
# of this run are KEPT, the problem is logged, nothing is pruned and the script exits 1. Retention keeps the newest
# AION_BACKUP_KEEP runs (default 14) of the nightly-* files; before-migrate-* and dashboard backups are never touched.
set -euo pipefail

DIR="${AION_BACKUP_DIR:-/var/lib/aion/backups}"
KEEP="${AION_BACKUP_KEEP:-14}"
TOOL="${AION_PG_TOOL:-/opt/aion/src/tools/postgres_dev.py}"
[[ "$KEEP" =~ ^[1-9][0-9]{0,3}$ ]] || { echo "AION_BACKUP_KEEP must be a whole number from 1 to 9999" >&2; exit 2; }
[[ -f "$TOOL" ]] || { echo "postgres_dev.py not found at $TOOL (set AION_PG_TOOL)" >&2; exit 1; }

STAMP="$(date -u +%Y%m%d-%H%M%S)"
FINAL="$DIR/nightly-$STAMP.dump"
cd /
echo "Backing up to $FINAL (+ one file per world)"
python3 "$TOOL" backup "$FINAL"

failed=0
for file in "$DIR/nightly-$STAMP.dump" "$DIR"/nightly-"$STAMP".world-*.dump; do
  [[ -s "$file" ]] || { echo "Backup file $file is missing or empty" >&2; failed=1; continue; }
  echo "Verifying $file (restore into a disposable database):"
  python3 "$TOOL" verify-backup "$file" || { echo "Verification failed for $file" >&2; failed=1; }
done
[[ $failed -eq 0 ]] || { echo "Backup kept but not verified; nothing pruned." >&2; exit 1; }

# Retention: the realm file names the run; its world files go with it.
mapfile -t old < <(find "$DIR" -maxdepth 1 -type f -name 'nightly-*.dump' -printf '%f\n' \
  | grep -E '^nightly-[0-9]{8}-[0-9]{6}\.dump$' | sort -r | tail -n +$((KEEP + 1)))
for name in "${old[@]}"; do
  rm -f -- "$DIR/$name" "$DIR/${name%.dump}".world-*.dump
  echo "Removed old backup $name"
done
echo "Backup saved ($(du -ch "$DIR/nightly-$STAMP"*.dump | tail -1 | cut -f1))"
