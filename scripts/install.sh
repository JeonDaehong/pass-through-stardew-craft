#!/bin/sh
# Builds the SMAPI mod (deploys it into the game's Mods folder) and copies the guest
# executable and extracted StarCraft data next to it, where the mod looks by default.
#
#   sh scripts/install.sh "C:/Program Files (x86)/Steam/steamapps/common/Stardew Valley"
set -e
GAME_DIR=${1:?usage: install.sh <Stardew Valley install dir>}
MOD_DIR="$GAME_DIR/Mods/StardewCraft"

[ -f build/stardewcraft_guest.exe ] || { echo "run scripts/build-guest.sh first"; exit 1; }
[ -f data/bw/arr/units.dat ] || { echo "run scripts/extract-data.sh first"; exit 1; }

dotnet build host/StardewCraft -c Release "-p:GamePath=$GAME_DIR"

mkdir -p "$MOD_DIR/guest" "$MOD_DIR/bw-data"
cp build/stardewcraft_guest.exe "$MOD_DIR/guest/"
cp -r data/bw/. "$MOD_DIR/bw-data/"
echo "installed to $MOD_DIR"
