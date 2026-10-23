#!/usr/bin/env bash
# Downloads the real-world schemas the QA sweep uses into $RW_DATA (default: tools/diffqa/work/realworld/rw-data,
# which is not tracked: the files are other projects' work, under their own licences, and are not part of this repository).
# Every source is pinned to a commit or tag, and every file is checked against the SHA-256 it had when the sweep was
# first run (2026-10-15), so a future run tests the same bytes. Needs curl, python, and sha256sum.
# Usage: fetch.sh [name ...]   (names as in the table below; none means all)
set -eu
pw() { (cd "$1" && { pwd -W 2>/dev/null || pwd; }); }  # a Windows path under Git Bash, which docker, python, and the CLI need
here=$(pw "$(dirname "$0")")
data=${RW_DATA:-$here/../work/realworld/rw-data}
mkdir -p "$data"
data=$(pw "$data")
gh_raw() { echo "https://raw.githubusercontent.com/$1/$2/$3"; }

# name | url | sha256 of the file
sources=(
"adventureworks|$(gh_raw lorint/AdventureWorks-for-Postgres b474991f0df1c4bf55ca4735eb0254ca0709eed2 install.sql)|27f71c99365aa5f44dba5d3b7ca092a654fcf50e6931c4dbe96ba2a13f8b7e0e"
"chinook|$(gh_raw lerocha/chinook-database 7f67772503d71ba90f19283c38e93923addb43fa ChinookDatabase/DataSources/Chinook_PostgreSql.sql)|e3fde5c1a5b51a2a91429a702c9ca6e69ba56e6c7f5e112724d70c3d03db695e"
"gitlab|$(gh_raw gitlabhq/gitlabhq b5f500282b2873417ba6b0e6e198f5a862111aa0 db/structure.sql)|ac395b890e4a4a4271394f417dc35073cc30df65586a2607642e58181b9780a8"
"northwind|$(gh_raw pthom/northwind_psql cd0ef28d66369fbe177778e604e4be0f153c9e5c northwind.sql)|0ee30c01ba282f7194f38bf7f99cd6be0470b7ee5f67d0f7ca41fb058d735e0c"
"osm|$(gh_raw openstreetmap/openstreetmap-website 192394b35ba2725b32315c5bc3722cae95661bef db/structure.sql)|daca3633ffcec2cdaab81c139ba9ac6e2ab4ddb738810e2ef04c903aa7e2db00"
"pagila|$(gh_raw devrimgunduz/pagila ce6e114236bb828e5ec0dc835f65f1bf1b9179f5 pagila-schema.sql)|071ee940a73c8f4fad2997185788065291607e2c55559e3747959e8275391536"
"plausible|$(gh_raw plausible/analytics 598928e7db29844a5cab37a396c567d1dae07c1b priv/repo/structure.sql)|175f8b5af1935edcc9094bfbdfb53395ab479c382ce54203906dd998342c15c4"
"supabase|$(gh_raw supabase/postgres 8b0ef1ef942e18493bf5c89087eff7b030c0e26d migrations/schema-17.sql)|213abc5ef3fabc5fc6a13e5b1020cab7e039f6488c8f2936806f7fc838e80a48"
"synapse|$(gh_raw element-hq/synapse 98735bc6db96a67542e52278f74f38b1480e329a synapse/storage/schema/main/full_schemas/72/full.sql.postgres)|93ee08d4584d15a23b0cc08e903dfed50818f559e4a66642a756cc204fbb8d37"
"synapse54|$(gh_raw element-hq/synapse v1.30.0 synapse/storage/databases/main/schema/full_schemas/54/full.sql.postgres)|cadfbe06c6cddbbeb6753fa12f554881a880ca410d39997ca9a4cade0b7f4171"
"temporal|$(gh_raw temporalio/temporal 1872297dceb4f82309ef3ba0922c688212417e34 schema/postgresql/v12/temporal/schema.sql)|1b0d5e8f40a26fce3e355802ebffc9fe917b121b7af095784135bb5eb8873fa6"
)
# MusicBrainz has no single file: its admin/sql files, joined in this order with a blank line after each.
mb_commit=7224306c92bbc23dfd2ee2cbc78977a9f91fafee
mb_files=(CreateTypes CreateTables CreateFunctions CreatePrimaryKeys CreateIndexes CreateFKConstraints CreateViews CreateTriggers)
mb_sha=881b2412840dcf0cdf470defa93360be48365f05e486f2c1e32f7ddb4f139c46

WANT=("$@")
check() { # file expected-sha
  local got; got=$(sha256sum "$1" | cut -d' ' -f1)
  if [ "$got" != "$2" ]; then echo "MISMATCH $1: expected $2, got $got" >&2; return 1; fi
  echo "ok $(basename "$1")"
}
selected() { [ ${#WANT[@]} -eq 0 ] && return 0; for w in "${WANT[@]}"; do [ "$w" = "$1" ] && return 0; done; return 1; }

for row in "${sources[@]}"; do
  IFS='|' read -r name url sha <<<"$row"
  selected "$name" || continue
  if [ -s "$data/$name.sql" ] && [ "$(sha256sum "$data/$name.sql" | cut -d' ' -f1)" = "$sha" ]; then echo "ok $name.sql (already there)"; continue; fi
  curl -sfL -m 120 -o "$data/$name.sql" "$url" && check "$data/$name.sql" "$sha"
done
if selected musicbrainz; then
  if [ -s "$data/musicbrainz.sql" ] && [ "$(sha256sum "$data/musicbrainz.sql" | cut -d' ' -f1)" = "$mb_sha" ]; then echo "ok musicbrainz.sql (already there)"; else
    : > "$data/musicbrainz.sql"
    for f in "${mb_files[@]}"; do
      curl -sfL -m 120 "https://raw.githubusercontent.com/metabrainz/musicbrainz-server/$mb_commit/admin/sql/$f.sql" >> "$data/musicbrainz.sql"; echo >> "$data/musicbrainz.sql"
    done
    check "$data/musicbrainz.sql" "$mb_sha"
  fi
fi

# Variants the sweep loads instead of the originals (the originals load into the wrong database or need a server the image lacks).
python - "$data" <<'PY'
import hashlib, os, sys
d = sys.argv[1]
def rd(n):
    p = os.path.join(d, n)
    if not os.path.exists(p):
        print('missing ' + n)
        return None
    return open(p, encoding='utf-8', newline='').read()
def wr(n, s, sha):
    open(os.path.join(d, n), 'w', encoding='utf-8', newline='').write(s)
    got = hashlib.sha256(s.encode('utf-8')).hexdigest()
    print(('ok ' if got == sha else 'MISMATCH ') + n)
# Chinook's script does DROP/CREATE DATABASE chinook and \c chinook, so it loads into a database of its own.
s = rd('chinook.sql')
if s is not None:
    drop = {'DROP DATABASE IF EXISTS chinook;', 'CREATE DATABASE chinook;', '\c chinook;'}
    wr('chinook_fixed.sql', ''.join(l for l in s.splitlines(True) if l.strip() not in drop),
       'c1c06b968d4061aab3fd72215ad814d9c80cc15e400b8b9f880f0d90f99a07fe')
# MusicBrainz: the objects its installer makes outside SQL (cube, earthdistance, the collation, the schema), and no BEGIN/COMMIT.
s = rd('musicbrainz.sql')
if s is not None:
    pre = ("CREATE EXTENSION IF NOT EXISTS cube; CREATE EXTENSION IF NOT EXISTS earthdistance;\n"
           "CREATE COLLATION musicbrainz (provider = icu, locale = '@colCaseFirst=lower;colNumeric=yes');\n")
    body = ''.join(l for l in s.splitlines(True) if l.strip() not in ('BEGIN;', 'COMMIT;'))
    body = body.replace('SET search_path = musicbrainz, public;', 'SET search_path = public;').replace('COLLATE musicbrainz.musicbrainz', 'COLLATE public.musicbrainz')
    wr('musicbrainz2.sql', pre + body, '94655927b304b1de31aa031128b0598e30213c6d2fd3a9cc07c2bdfc339e19ce')
# Supabase: the vault extension is not in the pgvector image.
s = rd('supabase.sql')
if s is not None:
    drop = {'-- Name: supabase_vault; Type: EXTENSION; Schema: -; Owner: -', 'CREATE EXTENSION IF NOT EXISTS supabase_vault WITH SCHEMA vault;',
            '-- Name: EXTENSION supabase_vault; Type: COMMENT; Schema: -; Owner: -', "COMMENT ON EXTENSION supabase_vault IS 'Supabase Vault Extension';"}
    wr('supabase2.sql', ''.join(l for l in s.splitlines(True) if l.strip() not in drop),
       '2a61a93eacceafcff7b99db9547ef732cc58f480faec248419a8bb44b6178811')
PY
