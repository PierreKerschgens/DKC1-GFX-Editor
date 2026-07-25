#!/usr/bin/env bash
# Fetches the libretro cores the V3 emulator harness runs against.
#
# The cores are NOT committed (binaries, ~4 MB, platform-specific) -- run this
# once after checkout. Override PLATFORM for a non-arm64-macOS machine, e.g.
#   PLATFORM=apple/osx/x86_64 ./fetch-cores.sh
#   PLATFORM=linux/x86_64    ./fetch-cores.sh   (cores are .so there, see below)
#
# snes9x  -- fast, used for the bulk of the frame-capture runs
# bsnes_mercury_balanced -- accurate, used to confirm anything snes9x flags
set -euo pipefail

PLATFORM="${PLATFORM:-apple/osx/arm64}"
EXT="${EXT:-dylib}"
BUILDBOT="https://buildbot.libretro.com/nightly/${PLATFORM}/latest"
CORES=(snes9x bsnes_mercury_balanced)

cd "$(dirname "$0")"
mkdir -p cores
cd cores

for core in "${CORES[@]}"; do
    archive="${core}_libretro.${EXT}.zip"
    echo "fetching ${core} ..."
    curl -sSf -m 300 -O "${BUILDBOT}/${archive}"
    unzip -o -q "${archive}"
    rm -f "${archive}"
done

# Downloaded dylibs carry a quarantine xattr; dlopen refuses them until it's cleared.
xattr -dr com.apple.quarantine . 2>/dev/null || true

ls -l ./*."${EXT}"
