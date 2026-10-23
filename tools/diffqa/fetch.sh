#!/bin/bash
# Downloads PostgreSQL's regression SQL (src/test/regress/sql/*.sql) for $DIFFQA_BRANCH into $DIFFQA_REGRESS.
# The files are test data: they are only ever run with psql against a throwaway container.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
. "$here/config.sh"
mkdir -p "$DIFFQA_REGRESS"
gh api "repos/postgres/postgres/contents/src/test/regress/sql?ref=$DIFFQA_BRANCH" --jq '.[] | select(.name | endswith(".sql")) | .name' > "$DIFFQA_REGRESS/.list"
while read -r file; do
  [ -s "$DIFFQA_REGRESS/$file" ] || curl -sfL -o "$DIFFQA_REGRESS/$file" "https://raw.githubusercontent.com/postgres/postgres/$DIFFQA_BRANCH/src/test/regress/sql/$file"
done < "$DIFFQA_REGRESS/.list"
echo "$(wc -l < "$DIFFQA_REGRESS/.list") files in $DIFFQA_REGRESS"
