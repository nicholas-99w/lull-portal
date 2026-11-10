#!/bin/sh
# Database backups for the Lull portal. Runs in the compose "backup" service
# (the postgres image, so pg_dump matches the server). Connection settings
# come from the standard PG* environment variables.
#
#   (no arguments)      back up now, then every day at BACKUP_AT (TZ)
#   --now               back up once and exit (run before every update)
#   --check             healthy if the newest backup is under 26 hours old
#   --restore FILE      replace the database with FILE (stop the portal first),
#                       then give every Mac's settings a new revision
#
# Backups are /backups/lull-<date>-<time>.dump (pg_dump custom format,
# compressed), kept for KEEP_DAYS days. They hold password hashes, so only
# root can read them: protect the folder like the database itself.
set -eu
DIR=/backups
KEEP_DAYS=${KEEP_DAYS:-14}
BACKUP_AT=${BACKUP_AT:-02:00}
umask 077

log() { echo "backup: $*"; }
die() { echo "backup: $*" >&2; exit 1; }

backup() {
    name="lull-$(date +%Y%m%d-%H%M%S).dump"
    # Written under a temporary name and renamed when complete, so a dump cut
    # short (full disk, restart) never looks like a good backup.
    pg_dump --format=custom --file="$DIR/.$name.partial" || { rm -f "$DIR/.$name.partial"; log "pg_dump failed"; return 1; }
    mv "$DIR/.$name.partial" "$DIR/$name"
    find "$DIR" -maxdepth 1 -name 'lull-*.dump' -mtime +"$KEEP_DAYS" -delete
    log "wrote $name ($(du -h "$DIR/$name" | cut -f1)); $(find "$DIR" -maxdepth 1 -name 'lull-*.dump' | wc -l) kept"
}

restore() {
    [ -f "$1" ] || die "no such file: $1 (give the path inside the container, e.g. /backups/lull-...dump)"
    # A running portal holds locks the restore would wait on, and would see a
    # half-restored database. lull_app is the portal's database user.
    active=$(psql -tAc "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND usename = 'lull_app'")
    [ "$active" = 0 ] || die "the portal is still connected: run 'docker compose stop portal' first"
    log "replacing database $PGDATABASE with $1"
    # One transaction: if anything fails, the database is left as it was.
    # Ownership and grants are left to the next deploy's migrate step, which
    # sets up the portal's database user (it may not exist on a new server).
    pg_restore --clean --if-exists --no-owner --no-privileges --single-transaction --exit-on-error \
        --dbname="$PGDATABASE" "$1"
    bump_revisions || die "restored, but couldn't give the Macs' settings new revisions: run '--restore' again, or the same UPDATE by hand"
    log "restored. Now run: docker compose up -d --wait"
}

# Each Mac caches the newest settings revision it has had and refuses an older
# one, so after going back to a backup every Mac would refuse the portal's
# (older) settings until someone changed them. A new revision for every Mac,
# as the portal makes them (never lower than now in Unix milliseconds), so each
# takes the restored settings at its next poll. Audited like any other change.
bump_revisions() {
    psql -v ON_ERROR_STOP=1 -q <<'SQL'
BEGIN;
WITH bumped AS (
    UPDATE device_settings
    SET revision = GREATEST(revision + 1, (extract(epoch FROM clock_timestamp()) * 1000)::bigint),
        updated_by = 'system (restore)', updated_at = now()
    RETURNING device_id
)
INSERT INTO audit_log (at, actor, action, detail)
SELECT now(), 'system', 'restored_from_backup', jsonb_build_object('macs', count(*)) FROM bumped;
COMMIT;
SQL
}

case "${1:-}" in
    --now) backup ;;
    --check) find "$DIR" -maxdepth 1 -name 'lull-*.dump' -mmin -1560 | grep -q . ;;
    --restore) [ $# -eq 2 ] || die "usage: --restore /backups/<file>"; restore "$2" ;;
    "")
        echo "$BACKUP_AT" | grep -Eq '^([01][0-9]|2[0-3]):[0-5][0-9]$' || die "BACKUP_AT must be HH:MM (24-hour), not '$BACKUP_AT'"
        at=$(echo "$BACKUP_AT" | tr -d :)
        # As PID 1, sh would ignore `docker compose stop` until killed 10 s later.
        trap 'exit 0' TERM INT
        backup
        # The first backup at or after BACKUP_AT each day. Checked every minute
        # rather than sleeping until a computed time: on a daylight saving night
        # BACKUP_AT may not exist (it runs when the clock passes it instead) or
        # may happen twice (it runs once).
        [ "$(date +%H%M)" -lt "$at" ] && done_day="" || done_day=$(date +%F)
        while :; do
            sleep 60 & wait $!
            [ "$(date +%F)" != "$done_day" ] && [ "$(date +%H%M)" -ge "$at" ] || continue
            done_day=$(date +%F)
            # A failed night is logged and retried the next night; --check
            # turns the container unhealthy once no backup is under 26 hours old.
            backup || log "FAILED (see above)"
        done
        ;;
    *) die "unknown argument '$1' (see the top of backup.sh)" ;;
esac
