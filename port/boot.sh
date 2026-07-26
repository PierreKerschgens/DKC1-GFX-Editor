#!/bin/sh
# boot.sh — stage a built test ROM under one fixed filename, so the emulator
# reuses the same SRAM and save state instead of restarting at the intro.
#
# The emulator keys its .srm and .data.szsnes off the ROM's *filename*, so every
# new descriptive name (dk-JUMPHOLE.sfc, dk-TUMBLE-flatx.sfc, ...) costs a fresh
# intro, a save-file selection and a walk back to the test spot. Booting one
# fixed name — port/dk-BOOT.sfc — keeps all of that.
#
#   ./port/boot.sh port/dk-JUMPHOLE.sfc
#
# Then always open port/dk-BOOT.sfc in the emulator. Its saves persist across
# builds; the build under test is whatever was staged last.
#
# The descriptive name is still the one the tool writes and the one the specs
# cite — this only copies. That matters for two reasons:
#   - `--batch` chains onto `<out>.dkctool.json`, so a fixed --out would silently
#     spend the same allocations twice (HANDOFF: the 54/54 → 37/54 trap).
#   - A fixed name cannot be identified at a glance, which is exactly the hazard
#     distinct names were adopted to avoid. Hence dk-BOOT.what, below.

set -eu

BOOT="port/dk-BOOT.sfc"
STAMP="port/dk-BOOT.what"

if [ $# -ne 1 ]; then
    if [ -f "$STAMP" ]; then
        printf 'Currently staged in %s:\n' "$BOOT"
        cat "$STAMP"
    else
        printf 'Nothing staged in %s yet.\n' "$BOOT"
    fi
    printf '\nusage: %s <built-rom.sfc>\n' "$0"
    exit 1
fi

SRC="$1"
[ -f "$SRC" ] || { printf 'no such ROM: %s\n' "$SRC" >&2; exit 1; }

# Copy the bytes only. dk-BOOT.srm and dk-BOOT.data.szsnes are deliberately left
# alone — they are the save game and last state, and carrying them over is the
# entire point.
cp "$SRC" "$BOOT"

printf '%s\nstaged %s\nsha %s\n' \
    "$SRC" \
    "$(date '+%Y-%m-%d %H:%M:%S')" \
    "$(shasum -a 256 "$SRC" | cut -c1-16)" > "$STAMP"

printf 'staged %s -> %s\n' "$SRC" "$BOOT"
[ -f "port/dk-BOOT.srm" ] && printf 'save game kept (dk-BOOT.srm)\n'
[ -d "port/dk-BOOT.data.szsnes" ] && printf 'save states kept (dk-BOOT.data.szsnes)\n'
exit 0
