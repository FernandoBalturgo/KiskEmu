"""Prepares the project's PostgreSQL cluster before the db container runs it in the foreground (deploy/docker/README.md).

Runs as the aion user with AION_PG_HOME (the a2core-postgres volume) and AION_PG_BIN (the image's PostgreSQL). It reuses
tools/postgres_dev.py, which never replaces an existing cluster's credentials. On top of that it:
  - refuses to touch a cluster made by another PostgreSQL major version (no initdb, no migration, no start);
  - refuses a non-empty data directory that is not a cluster (never initdb over files);
  - takes a backup (the realm database and every world database, postgres_dev.backup) into the backups folder before
    applying new migrations to a cluster that has them pending. That includes the one-time split of a single-database
    cluster (the A2Core layout) into the realm and world databases (migrations 092/093);
  - imports the catalogue into the realm database only when it has none (like deploy/linux/install.sh on a first
    install), then ensure_worlds: a database for every managed world (catalogue, starters, class skills, equipment
    rules), migrated.
Leaves the cluster stopped; the entrypoint then starts postgres in the foreground.
"""
import os
import subprocess
import sys
import time
from pathlib import Path

TOOLS = Path(os.environ.get('AION_PG_TOOL', '/opt/aion/src/tools/postgres_dev.py')).parent
sys.path.insert(0, str(TOOLS))
import postgres_dev as pg  # noqa: E402

DATA_DIR = pg.HOME / 'data'
GAME_DATA = Path(os.environ.get('AION_GAME_DATA', '/var/lib/aion/data'))
BACKUPS = Path(os.environ.get('AION_BACKUP_DIR', '/var/lib/aion/backups'))


def say(text):
    print(f'[db-prepare] {text}', flush=True)


def binary_major():
    out = subprocess.run([str(pg.BIN / 'postgres'), '--version'], capture_output=True, text=True, check=True).stdout
    return out.split()[-1].split('.')[0]


def check_cluster():
    """True for an existing cluster of this version, False for none; exits for anything it must not touch."""
    version_file = DATA_DIR / 'PG_VERSION'
    if version_file.exists():
        cluster = version_file.read_text().strip()
        if cluster != binary_major():
            say(f'The cluster in {DATA_DIR} was made by PostgreSQL {cluster}; this image has {binary_major()}. '
                'Nothing was changed. Build the image with PG_MAJOR=' + cluster + ' or follow "Upgrading PostgreSQL" in '
                'deploy/docker/README.md.')
            sys.exit(3)
        if not (pg.HOME / 'credentials.json').exists():
            say(f'{DATA_DIR} holds a cluster but {pg.HOME}/credentials.json is missing. Nothing was changed; restore the '
                'credentials file from a backup of the volume.')
            sys.exit(3)
        return True
    if DATA_DIR.exists() and any(DATA_DIR.iterdir()):
        say(f'{DATA_DIR} is not empty but holds no cluster (no PG_VERSION). Nothing was changed; check the volume.')
        sys.exit(3)
    return False


def applied(database):
    result = pg.sql('SELECT version FROM public.schema_migrations;', database=database, check=False)
    return set(result.stdout.split()) if result.returncode == 0 else set()


def pending_migrations():
    """Migration steps not yet applied: the realm's, the split of a single-database cluster, each world database's."""
    pending = []
    done = applied(pg.REALM)
    pending += [f.name for f in pg.migration_files('realm') if f.name not in done]
    if not pg.realm_is_split():
        pending.append('split into realm and world databases (092/093)')
    for world_id, name in sorted(pg.local_world_databases().items()):
        done = applied(name)
        pending += [f'{name}: {f.name}' for f in pg.migration_files('world') if f.name not in done]
    return pending


def backup_before_migrations(pending):
    BACKUPS.mkdir(parents=True, exist_ok=True)
    final = BACKUPS / f"before-migrate-{time.strftime('%Y%m%d-%H%M%S', time.gmtime())}.dump"
    say(f'{len(pending)} pending migration step(s) ({", ".join(pending)}): backing up first to {final} (+ one file per world)')
    for path in pg.backup(final):
        say(f'  saved {path}')


def realm_catalogue_empty():
    result = pg.sql('SELECT count(*) FROM catalog.builds;', check=False)
    return result.returncode != 0 or result.stdout.strip() in ('', '0')


def main():
    existing = check_cluster()
    if existing:
        pg.start()
        if pg.database_exists(pg.REALM):
            pending = pending_migrations()
            if pending:
                backup_before_migrations(pending)
    try:
        pg.setup()   # new cluster: initdb, roles, realm database; every start: roles and passwords, then realm migrations
        catalog = GAME_DATA / 'game-catalog-v2'
        if not (catalog / 'manifest.json').exists():
            raise SystemExit(f'[db-prepare] No game catalogue in {catalog}: put aion-server-data.tar.gz in ./data (README).')
        if realm_catalogue_empty():
            say('First install: importing the catalogue into the realm database.')
            say(str(pg.import_catalog(catalog)))
        for name in pg.ensure_worlds(catalog):
            say(f'World database ready: {name}')
        say('Database ready; migrations applied.')
    finally:
        pg.run('pg_ctl', ['stop', '-D', DATA_DIR, '-m', 'fast', '-w'], check=False)


if __name__ == '__main__':
    main()
