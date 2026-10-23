#!/bin/bash
# Runs run.sh for every regression file of $DIFFQA_BRANCH (or the names given), then writes the triage report.
# Usage: runall.sh [name ...]
set -u
here=$(cd "$(dirname "$0")" && pwd)
. "$here/config.sh"
names=("$@")
[ ${#names[@]} -eq 0 ] && mapfile -t names < <(ls "$DIFFQA_REGRESS"/*.sql | xargs -n1 basename | sed 's/\.sql$//')
for name in "${names[@]}"; do
  bash "$here/run.sh" "$name" > /dev/null 2>&1 || echo "run.sh failed for $name" >&2
done
python "$here/triage.py" "$DIFFQA_WORK/$DIFFQA_LABEL" > "$DIFFQA_WORK/$DIFFQA_LABEL/triage.md"
echo "triage: $DIFFQA_WORK/$DIFFQA_LABEL/triage.md"
