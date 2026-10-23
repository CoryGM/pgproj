# Keeps the stanzas of an apt Packages index whose file is one of a channel's packages and whose architecture is the one asked for (G-331).
# Usage: awk -v names="<file names, space-separated>" -v arch=<amd64|arm64> -f filter-packages.awk <Packages>
BEGIN {
    RS = ""
    ORS = "\n\n"
    n = split(names, list, " ")
    for (i = 1; i <= n; i++) wanted[list[i]] = 1
}
{
    file = ""
    architecture = ""
    m = split($0, lines, "\n")
    for (i = 1; i <= m; i++) {
        if (lines[i] ~ /^Filename: /) { file = lines[i]; sub(/^Filename: .*\//, "", file) }
        if (lines[i] ~ /^Architecture: /) { architecture = lines[i]; sub(/^Architecture: /, "", architecture) }
    }
    if ((file in wanted) && architecture == arch) print
}
