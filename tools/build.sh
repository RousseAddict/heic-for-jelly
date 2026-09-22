#!/bin/sh
#
# Builds the plugin on the Monterey machine and, with --deploy, installs it into
# Jellyfin.
#
# The dev Mac runs Big Sur, which .NET 9 does not support, so compilation is
# delegated over ssh. The remote tree is a disposable mirror: it is rsynced from
# this repository every run with --delete, and never edited by hand.
#
# Usage:
#   sh tools/build.sh              # sync + build
#   sh tools/build.sh --deploy     # sync + build + copy into Jellyfin (no restart)
#
# Both machines are named by environment variable, with no default: the addresses
# are somebody's private network and do not belong in a published repository. Copy
# tools/build.env.example to tools/build.env, which is gitignored and sourced below.

set -eu

repo=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)

# Only consulted for what the environment has not already set, so that a one-off
# `JELLYFIN_HOST=… sh tools/build.sh --deploy` still wins over the file.
if [ -z "${BUILD_HOST:-}" ] || [ -z "${JELLYFIN_HOST:-}" ]; then
    # shellcheck source=/dev/null
    [ -f "$repo/tools/build.env" ] && . "$repo/tools/build.env"
fi

BUILD_DIR=${BUILD_DIR:-'~/build/heic-for-jelly'}
JELLYFIN_PLUGINS=${JELLYFIN_PLUGINS:-'~/Documents/jellyfin/config/plugins'}

: "${BUILD_HOST:?set BUILD_HOST (user@host of a machine with .NET 9) — see tools/build.env.example}"

PLUGIN=Jellyfin.Plugin.HeicForJelly
VERSION=1.0.0.0
PROJ="src/$PLUGIN/$PLUGIN.csproj"
OUT="src/$PLUGIN/bin/Release/net9.0/$PLUGIN.dll"

deploy=0
[ "${1:-}" = "--deploy" ] && deploy=1

echo "==> sync to $BUILD_HOST"
rsync -az --delete \
    --exclude '.git' --exclude 'bin' --exclude 'obj' --exclude 'scratch' \
    "$repo/" "$BUILD_HOST:$BUILD_DIR/"

echo "==> build"
ssh "$BUILD_HOST" "export DOTNET_ROOT=/usr/local/share/dotnet
export PATH=\"\$DOTNET_ROOT:\$PATH\"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
cd $BUILD_DIR && dotnet build '$PROJ' -c Release"

[ "$deploy" -eq 1 ] || exit 0

: "${JELLYFIN_HOST:?set JELLYFIN_HOST (user@host running Jellyfin) — see tools/build.env.example}"

# Jellyfin discovers plugins by folder, and the folder name carries the version.
target="$JELLYFIN_PLUGINS/HeicForJelly_$VERSION"

echo "==> deploy to $JELLYFIN_HOST:$target"
ssh "$JELLYFIN_HOST" "mkdir -p $target"
ssh "$BUILD_HOST" "cat $BUILD_DIR/$OUT" |
    ssh "$JELLYFIN_HOST" "cat > $target/$PLUGIN.dll"
ssh "$BUILD_HOST" "cat $BUILD_DIR/src/$PLUGIN/meta.json" |
    ssh "$JELLYFIN_HOST" "cat > $target/meta.json"

echo
echo "Installed. Jellyfin loads plugins at startup only, so it must be restarted:"
echo "    ssh $JELLYFIN_HOST 'podman restart jellyfin'"
echo "That interrupts any playback in progress, which is why this script will not"
echo "do it for you."
