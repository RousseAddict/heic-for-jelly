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

set -eu

BUILD_HOST=${BUILD_HOST:-the build host}
BUILD_DIR=${BUILD_DIR:-'~/build/heic-for-jelly'}
JELLYFIN_HOST=${JELLYFIN_HOST:-the photo host}
JELLYFIN_PLUGINS=${JELLYFIN_PLUGINS:-'~/Documents/jellyfin/config/plugins'}

PLUGIN=Jellyfin.Plugin.HeicForJelly
VERSION=1.0.0.0
PROJ="src/$PLUGIN/$PLUGIN.csproj"
OUT="src/$PLUGIN/bin/Release/net9.0/$PLUGIN.dll"

deploy=0
[ "${1:-}" = "--deploy" ] && deploy=1

repo=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)

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
