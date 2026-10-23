# shellcheck shell=bash disable=SC2034  # sourced; gpg_sign and fingerprint are used by the script that sources it
# Sourced inside a container (G-331): imports the signing key from /signing/key.asc and sets $fingerprint, for gpg and rpmsign to sign with.
gpg --batch --quiet --import /signing/key.asc 2>/dev/null
fingerprint="$(gpg --batch --with-colons --list-secret-keys | awk -F: '/^fpr/ { print $10; exit }')"
[[ -n "$fingerprint" ]] || { echo "no secret key in /signing/key.asc" >&2; exit 1; }
# gpg signs without asking: the passphrase comes from /signing/passphrase (empty for a key without one).
gpg_sign=(gpg --batch --yes --pinentry-mode loopback --passphrase-file /signing/passphrase --local-user "$fingerprint")
