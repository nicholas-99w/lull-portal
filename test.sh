#!/bin/bash
# Runs the portal's tests. Integration tests need Postgres: this starts a
# throwaway postgres:17 container (lull-portal-testdb, localhost:55432) and
# removes it when the run ends, pass or fail (or Ctrl-C).
#   ./test.sh            all tests (extra arguments go to dotnet test)
#   ./test.sh --stop     remove a test database container left by a killed run
set -euo pipefail
cd "$(dirname "$0")"
export PATH="$HOME/.docker/bin:/usr/local/bin:$PATH"
NAME=lull-portal-testdb
PORT=55432

# -v: also the anonymous volume the postgres image creates for its data,
# which would otherwise be left behind (one per run).
remove_db() { docker rm -f -v "$NAME" >/dev/null 2>&1 || true; }

if [[ "${1:-}" == "--stop" ]]; then
    remove_db
    exit 0
fi

# Start clean every time (a leftover from a killed run would hold the port).
remove_db
trap remove_db EXIT
docker run -d --name "$NAME" -p "127.0.0.1:${PORT}:5432" \
    -e POSTGRES_USER=lull -e POSTGRES_PASSWORD=lull postgres:17 >/dev/null
ready=0
for _ in $(seq 1 60); do
    # -h: the TCP listener, which only comes up after the image's init scripts.
    if docker exec "$NAME" pg_isready -U lull -h 127.0.0.1 >/dev/null 2>&1; then
        ready=1
        break
    fi
    sleep 1
done
[[ $ready -eq 1 ]] || { echo "test.sh: Postgres didn't start within 60 s" >&2; exit 1; }

LULL_TEST_POSTGRES="Host=127.0.0.1;Port=${PORT};Username=lull;Password=lull;Database=postgres" \
    dotnet test Lull.Portal.slnx "$@"
