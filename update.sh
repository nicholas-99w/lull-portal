#!/bin/sh
# Updates a running portal on its Docker host: docs/administration.md, "Update".
# Run it from anywhere; it works in the folder it's in (the one with compose.yml
# and .env). Stops at the first step that fails, so a failed backup never goes on
# to replace the portal.
#
#   ./update.sh          the latest release (the newest v* tag on main)
#   ./update.sh --main   the latest main, released or not (for testing)
set -eu

log() { echo "update: $*"; }
die() { echo "update: $*" >&2; exit 1; }

usage() {
    echo "usage: ./update.sh [--main]"
    echo "  (none)  deploy the latest release: the newest v* tag on main"
    echo "  --main  deploy the latest main, released or not (for testing)"
}

# The newest release tag (v1.2.3), by version rather than date: one that's on
# main, so a tag pushed anywhere else is never deployed.
latest_release() {
    for tag in $(git tag --list 'v[0-9]*' --sort=-version:refname); do
        if git merge-base --is-ancestor "$tag" origin/main; then
            echo "$tag"
            return
        fi
    done
}

# Everything runs from main, called on the last line: the shell has then read
# the whole script before git can replace this file.
main() {
    target=release
    for arg in "$@"; do
        case "$arg" in
            --main) target=main ;;
            -h | --help) usage; exit 0 ;;
            *) usage >&2; die "unknown option: $arg" ;;
        esac
    done
    cd "$(dirname "$0")"
    [ -f .env ] || die "no .env in $(pwd): copy .env.example and set its secrets first"
    # Local changes would be overwritten by the checkout (or block it): .env and backups/ aren't tracked.
    git diff --quiet HEAD -- || die "this copy has local changes (git status): commit, stash or discard them first"

    log "fetching the latest portal"
    git fetch --quiet --tags --prune origin
    if [ "$target" = main ]; then
        git switch --quiet main
        # --ff-only: refuses (rather than merges) if this copy has local commits.
        git merge --quiet --ff-only origin/main
        log "on main at $(git rev-parse --short HEAD) (not necessarily released)"
    else
        tag=$(latest_release)
        [ -n "$tag" ] || die "no release tag on main yet: use --main"
        # Never back: the database may already have a newer version's migrations, which an
        # older portal refuses to start with. (A copy that followed --main may be ahead.)
        git merge-base --is-ancestor HEAD "$tag" \
            || die "this copy ($(git rev-parse --short HEAD)) is newer than the latest release, $tag: use --main, or wait for the next release"
        git -c advice.detachedHead=false checkout --quiet "$tag"
        log "on release $tag"
    fi

    log "backing up the database first, to roll back to"
    docker compose run --rm backup --now

    if [ "$target" = main ]; then
        # No image is published for main: build it here, under its own name, so
        # the release's name keeps meaning the published image.
        export LULL_PORTAL_IMAGE=lull-portal:main
        log "pulling Postgres's latest patch release"
        docker compose pull --ignore-buildable
        log "building the portal on the .NET base images' latest patches"
        docker compose build --pull
    else
        # The release's image (named in its compose.yml), rebuilt weekly on the
        # base images' latest patches, and Postgres's latest patch release.
        # An LULL_PORTAL_IMAGE in .env would win over the release's.
        if grep -q '^[[:space:]]*LULL_PORTAL_IMAGE=' .env; then
            die ".env sets LULL_PORTAL_IMAGE, which would replace the release's image: remove it first"
        fi
        log "pulling the portal's image and Postgres's latest patch release"
        docker compose pull
    fi

    log "migrating the database, then starting the new portal"
    docker compose up -d --wait

    docker compose ps
    if [ "$target" = main ]; then
        log "done. Other compose commands in this copy need LULL_PORTAL_IMAGE=lull-portal:main in front, until the next release"
    else
        log "done"
    fi
}

main "$@"
