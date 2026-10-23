"""Checks that a parity run's differing cases differ only by places on diagnostics (G-309e).

Usage: python tools/engine-parity/places-only.py <parity output folder>

For every case left under the folder (parity.sh keeps only the ones that differ), the printed output of both sides is compared with each diagnostic's
place taken out (`file:line:column-endLine:endColumn: [Severity]` and `file: [Severity]` become `[Severity]`), and a `--dump-model` file with the
`Location` of each diagnostic taken out. A case that still differs is listed; the exit code is 1 when there is one.
"""
import json
import os
import re
import sys

PLACE = re.compile(r"^(?:\[verbose\] )?\S.*?(?::\d+(?::\d+(?:-\d+:\d+)?)?)?: (\[(?:Error|Warning|Info)\])", re.MULTILINE)


def text(path):
    with open(path, encoding='utf-8', errors='replace') as handle:
        return PLACE.sub(r"\1", handle.read())


def model(path):
    with open(path, encoding='utf-8') as handle:
        data = json.load(handle)
    for key in ('Diagnostics', 'RecoveryDiagnostics'):
        for diagnostic in data.get(key) or []:
            diagnostic.pop('Location', None)
    return data


root = sys.argv[1]
still = []
checked = 0
for case in sorted(os.listdir(root)):
    base, new = os.path.join(root, case, 'base'), os.path.join(root, case, 'new')
    if not os.path.isdir(base) or not os.path.isdir(new):
        continue
    checked += 1
    for folder, _, files in os.walk(base):
        for name in files:
            left = os.path.join(folder, name)
            right = os.path.join(new, os.path.relpath(left, base))
            if not os.path.exists(right):
                still.append(f"{case}: {name} only in base")
                continue
            same = model(left) == model(right) if name.endswith('.json') and 'model' in name else text(left) == text(right)
            if not same:
                still.append(f"{case}: {os.path.relpath(left, base)}")
print(f"{checked} differing case(s) checked; {len(still)} differ by more than places")
for line in still:
    print('  ' + line)
sys.exit(1 if still else 0)
