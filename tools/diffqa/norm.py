"""Normalizes pg_dump --schema-only output for comparison: one statement per line (dollar-quoted bodies kept whole), whitespace collapsed,
SET / set_config / psql meta-commands dropped, sorted and de-duplicated, so two dumps differ only where their schemas do."""
import re
import sys

BACKSLASH = chr(92)


def statements(text):
    result, buffer, dollar = [], [], None
    for line in text.splitlines():
        if not buffer and (line.startswith('--') or not line.strip()):
            continue
        buffer.append(line)
        for tag in re.findall(r'\$(?:[A-Za-z_][A-Za-z_0-9]*)?\$', line):
            dollar = None if dollar == tag else (tag if dollar is None else dollar)
        if dollar is None and line.rstrip().endswith(';'):
            result.append(' '.join(part.strip() for part in buffer))
            buffer = []
    return result


def main(path):
    text = open(path, encoding='utf-8', errors='replace').read()
    kept = set()
    for statement in statements(text):
        if statement.startswith(('SET ', 'SELECT pg_catalog.set_config', BACKSLASH)):
            continue
        kept.add(re.sub(r'\s+', ' ', statement))
    print('\n'.join(sorted(kept)))


if __name__ == '__main__':
    main(sys.argv[1])
