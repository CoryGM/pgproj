#!/bin/bash
# Makes what the harness runs on, for $DIFFQA_VERSION (see config.sh): the Docker network, a postgres:$DIFFQA_VERSION-alpine server on it that publishes
# no host port, and a self-contained linux-x64 build of this checkout in $DIFFQA_LINUX, made only when it is missing (servers of several versions share it);
# `setup.sh build` makes it again from the checkout as it is now. `setup.sh down` removes the server (and the network, once no other harness server is on it).
set -u
here=$(cd "$(dirname "$0")" && pwd)
. "$here/config.sh"
if [ "${1:-}" = down ]; then
  docker rm -f "$DIFFQA_CONTAINER" >/dev/null 2>&1
  docker network rm "$DIFFQA_NETWORK" >/dev/null 2>&1 || echo "the network $DIFFQA_NETWORK is still in use, or already gone"
  exit 0
fi
docker network inspect "$DIFFQA_NETWORK" >/dev/null 2>&1 || docker network create "$DIFFQA_NETWORK" >/dev/null
if [ "${1:-}" != build ] || ! docker exec "$DIFFQA_CONTAINER" pg_isready -U postgres >/dev/null 2>&1; then
docker rm -f "$DIFFQA_CONTAINER" >/dev/null 2>&1
docker run -d --name "$DIFFQA_CONTAINER" --network "$DIFFQA_NETWORK" -e POSTGRES_PASSWORD="$DIFFQA_PASSWORD" "postgres:$DIFFQA_VERSION-alpine" >/dev/null
until docker exec "$DIFFQA_CONTAINER" pg_isready -U postgres >/dev/null 2>&1; do sleep 2; done
fi
if [ "${1:-}" = build ] || [ ! -f "$DIFFQA_LINUX/pgproj" ]; then
rm -rf "$DIFFQA_LINUX"
dotnet publish "$repo/src/PgProj.Cli" -c Release -r linux-x64 --self-contained -o "$DIFFQA_LINUX" > "$DIFFQA_WORK/publish.log" 2>&1 \
  || { echo "the Linux build failed: $DIFFQA_WORK/publish.log" >&2; exit 1; }
fi
echo "$DIFFQA_CONTAINER on $DIFFQA_NETWORK; pgproj $("${pgproj_cmd[@]}" --version 2>&1 | head -1)"
