#!/bin/sh
# Builds build/stardewcraft_guest.exe (OpenBW, headless) with zig.
# Requires: python + `pip install ziglang`, and `git submodule update --init`.
# Run from the repository root.
set -e
mkdir -p build
python -m ziglang c++ -target x86_64-windows-gnu -O2 -std=c++17 -w \
	-Ithird_party/openbw guest/guest.cpp -o build/stardewcraft_guest.exe
echo "built build/stardewcraft_guest.exe"
