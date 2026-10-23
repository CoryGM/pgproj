#!/usr/bin/env bash
# Downloads, from this repository's GitHub releases, the .deb and .rpm packages of each channel of the apt and dnf repositories (G-331):
#   stable   the newest <keep> releases that are not pre-releases (default 3), so a user can go back a version or two;
#   testing  the newest pre-release when it is newer than the newest release, and otherwise the newest release, so the channel always has one.
# Older versions are left out: GitHub Pages serves at most 1 GB, and each version's four packages are about 160 MB. Prints each channel's tags.
#
# Usage: packaging/linux/collect-releases.sh <stable dir> <testing dir> [keep]   (needs gh, authenticated; GH_TOKEN in a workflow)
set -euo pipefail

stable="${1:?usage: collect-releases.sh <stable dir> <testing dir> [keep]}"
testing="${2:?usage: collect-releases.sh <stable dir> <testing dir> [keep]}"
keep="${3:-3}"
mkdir -p "$stable" "$testing"

# Newest first; drafts have no packages to serve.
releases="$(gh release list --limit 100 --json tagName,isPrerelease,isDraft,publishedAt \
    --jq '[.[] | select(.isDraft | not)] | sort_by(.publishedAt) | reverse | .[] | "\(.tagName) \(.isPrerelease) \(.publishedAt)"')"

stable_tags=()
newest_stable_at=""
newest_pre_tag=""
newest_pre_at=""
while read -r tag prerelease published; do
    [[ -n "$tag" ]] || continue
    if [[ "$prerelease" == false ]]; then
        [[ -n "$newest_stable_at" ]] || newest_stable_at="$published"
        [[ ${#stable_tags[@]} -ge $keep ]] || stable_tags+=("$tag")
    elif [[ -z "$newest_pre_tag" ]]; then
        newest_pre_tag="$tag"
        newest_pre_at="$published"
    fi
done <<< "$releases"

testing_tag=""
if [[ -n "$newest_pre_tag" && ( -z "$newest_stable_at" || "$newest_pre_at" > "$newest_stable_at" ) ]]; then
    testing_tag="$newest_pre_tag"
elif [[ ${#stable_tags[@]} -gt 0 ]]; then
    testing_tag="${stable_tags[0]}"
fi

download() {
    gh release download "$1" --dir "$2" --pattern '*.deb' --pattern '*.rpm' --skip-existing
}
for tag in "${stable_tags[@]}"; do download "$tag" "$stable"; done
[[ -z "$testing_tag" ]] || download "$testing_tag" "$testing"

echo "stable: ${stable_tags[*]:-(none)}"
echo "testing: ${testing_tag:-(none)}"
