#!/bin/sh
# Extracts the files OpenBW needs from YOUR StarCraft: Remastered install into data/bw.
# The extracted files are Blizzard content: keep them on your machine, never commit or share them.
#
#   sh scripts/extract-data.sh "C:/Program Files (x86)/StarCraft" [out_dir]
set -e
STARCRAFT_DIR=${1:?usage: extract-data.sh <StarCraft install dir> [out_dir]}
OUT=${2:-data/bw}

[ -f build/casc_extract.exe ] || sh tools/casc-extract/build.sh

# Unit/weapon tables, animation scripts, melee triggers, tilesets and unit graphics.
./build/casc_extract.exe list "$STARCRAFT_DIR" | cut -f1 | tr -d '\r' | sed 's#\\#/#g' \
	| grep -iE '^(arr/[a-z]+\.(dat|tbl)|scripts/iscript\.bin|triggers/melee\.trg|TileSet/[a-zA-Z]+\.(cv5|vf4|vx4|vr4|wpe)|unit/.*\.(grp|lo[a-z]))$' \
	| sed 's#/#\\#g' > build/openbw_files.txt

echo "extracting $(wc -l < build/openbw_files.txt) files to $OUT"
./build/casc_extract.exe extract "$STARCRAFT_DIR" "$OUT" - < build/openbw_files.txt > build/extract.log
echo "done; see build/extract.log"
