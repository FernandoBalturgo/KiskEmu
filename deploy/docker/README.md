# KiskEmu in Docker (private branch)

Ported from the Docker setup of the a2ps repository and adapted to this repository. **Not built or run yet** (the machine
it was written on has no Docker). Layout: `docker-compose.yml` (ZeroTier container) or
`docker-compose.host-vpn.yml` (ZeroTier One / Hamachi on the host), `deploy/docker/` (image, entrypoint, database prepare,
backup), `.env` from `env.example`, the server data bundle in `./data`.

```bash
docker volume create a2core-postgres      # once; skip if it exists
cp deploy/docker/env.example .env         # set ZEROTIER_NETWORK_ID (or AION_BIND_IP + AION_PUBLIC_ADDRESS for host-vpn)
docker compose up -d --build
```

## What differs from the a2ps setup

- **Account site is HTTPS only.** KiskLauncher refuses `http://` addresses and `tools/server_env.py` here needs a
  certificate. `AION_ACCOUNT_HTTPS` is gone: a self-signed certificate is made for the address (or put your
  `cert.pem`/`key.pem` in the `state` volume under `tls/`). For a self-signed one the entrypoint sets
  `Aion__Launcher__CertificateThumbprint`, so the launcher ZIP from the account page pins it.
- **Launcher** is `KiskLauncher.exe` (no server built in); the account page offers a ZIP with this server's `launcher.json`.
- **Two kinds of databases.** One realm database plus one database per world (`aion`, `aion_world_<id>`).
  `db_prepare.py` follows `deploy/linux/install.sh`: migrate the realm, import the catalogue if it has none, then
  `ensure-worlds`. A cluster made by the single-database A2Core layout is **split** on the first start (migrations 092/093);
  a backup of everything is written to `./backups/before-migrate-*.dump` (+ one `.world-<id>.dump` per world) first.
  After the split that volume is no longer usable by the a2ps setup; restore the backup to go back.
- **Backups** (`backup.sh`): `nightly-<time>.dump` (realm) and `nightly-<time>.world-<id>.dump` per run, each verified.
  There is no `restore.sh` here: the a2ps one is for a single database. Restore manually with `pg_restore` from a `tools` run.
- **Rates / player cap / pvp options do not exist.** `AION_SERVER_ARGS` only takes what this repo's `tools/server_env.py`
  has (`--open-registration`, `--staff-port`, `--max-connections`, `--preload`, `--geolocation`, `--staff-path`).
  `AION_PG_TIMEOUT` is not read by this repo's `postgres_dev.py`.
- Extra ports: world servers started from the staff dashboard use game ports 13329-13340 (published by host-vpn mode).
