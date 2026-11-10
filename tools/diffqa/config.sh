# Settings for the differential QA harness; each can be overridden from the environment.
# No server publishes a host port: the server and pgproj (a self-contained linux-x64 build, run in a container) share the Docker network
# $DIFFQA_NETWORK, and pgproj reaches the server by its container name. setup.sh makes the network, the server, and the build.
export MSYS_NO_PATHCONV=1   # Git Bash would rewrite the /paths given to docker; these are paths inside the containers
pw() { (cd "$1" && { pwd -W 2>/dev/null || pwd; }); }  # a Windows path under Git Bash, which docker and python need
here=${here:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)}
here=$(pw "$here")
repo=$(pw "$here/../..")
: "${DIFFQA_VERSION:=18}"                                  # PostgreSQL major version under test
: "${DIFFQA_CONTAINER:=diffqa$DIFFQA_VERSION}"             # docker container running postgres:$DIFFQA_VERSION-alpine, on $DIFFQA_NETWORK
: "${DIFFQA_NETWORK:=diffqa}"                              # the Docker network the server and pgproj share
: "${DIFFQA_PASSWORD:=qa}"
: "${DIFFQA_BRANCH:=REL_${DIFFQA_VERSION}_STABLE}"         # postgres/postgres branch the regression SQL comes from
: "${DIFFQA_REGRESS:=$here/regress/$DIFFQA_BRANCH}"
: "${DIFFQA_WORK:=$here/work}"
mkdir -p "$DIFFQA_WORK"; DIFFQA_WORK=$(pw "$DIFFQA_WORK")  # mounted at /work in the pgproj container
: "${DIFFQA_LINUX:=$DIFFQA_WORK/pgproj-linux}"             # the Linux build setup.sh publishes; must be inside $DIFFQA_WORK
: "${DIFFQA_IMAGE:=mcr.microsoft.com/dotnet/runtime-deps:10.0}"
: "${DIFFQA_LABEL:=pg$DIFFQA_VERSION}"
: "${DIFFQA_TIMEOUT:=600}"
export DIFFQA_VERSION DIFFQA_CONTAINER DIFFQA_NETWORK DIFFQA_PASSWORD DIFFQA_BRANCH DIFFQA_REGRESS DIFFQA_WORK DIFFQA_LINUX DIFFQA_IMAGE DIFFQA_LABEL DIFFQA_TIMEOUT
# incontainer <path under $DIFFQA_WORK>: the same path as the pgproj container sees it.
incontainer() { echo "/work${1#"$DIFFQA_WORK"}"; }
# The pgproj command, as an array so that timeout can run it.
pgproj_cmd=(docker run --rm --network "$DIFFQA_NETWORK" -v "$DIFFQA_WORK:/work" -w /work "$DIFFQA_IMAGE" "$(incontainer "$DIFFQA_LINUX")/pgproj")
