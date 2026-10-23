"""Aggregates a differential QA run (one directory per regression file, as run.sh writes them) into a Markdown report:
per-file results, failures grouped by message, not-modeled warnings by statement kind, and dump differences by statement kind.
Usage: triage.py <work/label directory>"""
import collections
import os
import re
import sys

ANSI = re.compile(r'\x1b\[[0-9;]*m')


def read(path):
    try:
        return ANSI.sub('', open(path, encoding='utf-8', errors='replace').read())
    except FileNotFoundError:
        return ''


def pattern(message):
    """A message with its names and numbers taken out, so the same failure on different objects groups together."""
    message = re.sub(r"'[^']*'", "'...'", message)
    message = re.sub(r'"[^"]*"', '"..."', message)
    message = re.sub(r'\b\d+\b', 'N', message)
    message = re.sub(r'Key: \S+', 'Key: ...', message)
    return message.strip()[:220]


def statement_kind(statement):
    """The leading keywords of a statement, and the ALTER action, without names: CREATE TABLE, ALTER TABLE ONLY ATTACH PARTITION, COMMENT ON CONSTRAINT."""
    words = re.findall(r'[A-Za-z_]+|\(|;', statement)
    head = []
    for word in words[:4]:
        if word.upper() != word or word in ('(', ';'):
            break
        head.append(word)
    kind = ' '.join(head) or statement[:30]
    if kind.startswith('ALTER'):
        action = re.search(r'\b(ATTACH PARTITION|ADD GENERATED|SET DEFAULT|SET COMPRESSION|SET STORAGE|ADD CONSTRAINT|OWNED BY|REPLICA IDENTITY|SET STATISTICS|ADD OPERATOR|ADD FUNCTION|CLUSTER ON|ENABLE|DISABLE|INHERIT|SET \()', statement)
        if action:
            kind += ' ... ' + action.group(1)
    return kind


def main(root):
    names = sorted(d for d in os.listdir(root) if os.path.isfile(os.path.join(root, d, 'result.tsv')))
    rows, failures, warnings = [], collections.defaultdict(list), collections.Counter()
    missing = {'init': collections.defaultdict(list), 'written': collections.defaultdict(list)}
    for name in names:
        d = os.path.join(root, name)
        fields = read(os.path.join(d, 'result.tsv')).strip().split('\t')
        rows.append(fields)
        _, statements, wrc, wdiff, irc, iprc, idiff = (fields + [''] * 7)[:7]
        for path, label in (('i_init.out', 'init'), ('i_pub.out', 'init publish'), ('w_build.out', 'written build'), ('w_pub.out', 'written publish')):
            text = read(os.path.join(d, path))
            lines = text.splitlines()
            for index, line in enumerate(lines):
                if line.startswith(('Publish failed', 'Init failed', 'Build failed')) or '[Error]' in line:
                    detail = line
                    if line.startswith('Init failed') and index + 1 < len(lines):
                        detail = line + ' ' + lines[index + 1].strip()
                    failures[(label, pattern(detail))].append((name, detail.strip()[:300]))
            if path == 'w_build.out':
                for match in re.finditer(r'\[Warning\] (.*?is not modeled.*)', text):
                    warnings[pattern(re.sub(r"^.*?: ", '', match.group(1)))] += 1
        for path, key, ok in (('init.diff', 'init', iprc == '0'), ('written.diff', 'written', wrc == '0')):
            if not ok:
                continue
            for line in read(os.path.join(d, path)).splitlines():
                if line.startswith('< '):
                    missing[key][statement_kind(line[2:])].append((name, line[2:].strip()[:240]))

    out = ['# Differential QA triage', '', f'Run: `{root}`. {len(names)} regression files.', '']
    out += ['## Per file', '', '| file | statements | written publish | written diff | init | init publish | init diff |', '|---|---|---|---|---|---|---|']
    out += ['| ' + ' | '.join(row) + ' |' for row in rows]
    out += ['', '## Failures, grouped by message', '', 'Each is a build error or a failed `init` or `publish`. The init path is pgproj failing on its own output.', '']
    for (label, message), hits in sorted(failures.items(), key=lambda item: (-len({h[0] for h in item[1]}), item[0])):
        files = sorted({hit[0] for hit in hits})
        out.append(f'- **{label}** ({len(files)} files: {", ".join(files[:8])}{"..." if len(files) > 8 else ""}): `{message}`')
        out.append(f'  - e.g. {hits[0][0]}: `{hits[0][1]}`')
    for key in ('init', 'written'):
        out += ['', f'## Statements the {key} path lost or changed (only runs whose publish succeeded), by kind', '']
        for kind, hits in sorted(missing[key].items(), key=lambda item: -len(item[1])):
            files = sorted({hit[0] for hit in hits})
            out.append(f'- **{kind}**: {len(hits)} in {len(files)} files ({", ".join(files[:6])}{"..." if len(files) > 6 else ""})')
            out.append(f'  - e.g. `{hits[0][1]}`')
    out += ['', '## Not-modeled warnings on the written path, by message', '']
    out += [f'- {count} × `{message}`' for message, count in warnings.most_common()]
    print('\n'.join(out))


if __name__ == '__main__':
    main(sys.argv[1])
