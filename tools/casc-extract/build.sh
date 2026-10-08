#!/bin/sh
# Builds build/casc_extract.exe from CascLib sources using zig (pip install ziglang).
# Run from the repository root.
set -e
C=third_party/CascLib/src
OBJ=build/casc
mkdir -p "$OBJ"

for f in $C/jenkins/lookup3.c $C/zlib/adler32.c $C/zlib/crc32.c $C/zlib/inffast.c \
         $C/zlib/inflate.c $C/zlib/inftrees.c $C/zlib/zutil.c; do
	o="$OBJ/$(basename "$f" .c).o"
	[ -f "$o" ] || python -m ziglang cc -target x86_64-windows-gnu -O2 -w -c "$f" -o "$o"
done

# Same list as CascLib's CMakeLists.txt SRC_FILES.
python -m ziglang c++ -target x86_64-windows-gnu -O2 -std=c++17 -w -I$C \
	$C/common/Common.cpp $C/common/Directory.cpp $C/common/Csv.cpp $C/common/FileStream.cpp \
	$C/common/FileTree.cpp $C/common/ListFile.cpp $C/common/Mime.cpp $C/common/RootHandler.cpp \
	$C/common/Sockets.cpp $C/hashes/md5.cpp $C/hashes/sha1.cpp \
	$C/overwatch/apm.cpp $C/overwatch/cmf.cpp $C/overwatch/aes.cpp \
	$C/CascDecompress.cpp $C/CascDecrypt.cpp $C/CascDumpData.cpp $C/CascFiles.cpp \
	$C/CascFindFile.cpp $C/CascIndexFiles.cpp $C/CascOpenFile.cpp $C/CascOpenStorage.cpp \
	$C/CascReadFile.cpp $C/CascRootFile_Diablo3.cpp $C/CascRootFile_Install.cpp \
	$C/CascRootFile_MNDX.cpp $C/CascRootFile_Text.cpp $C/CascRootFile_TVFS.cpp \
	$C/CascRootFile_OW.cpp $C/CascRootFile_WoW.cpp \
	tools/casc-extract/casc_extract.cpp "$OBJ"/*.o -lws2_32 -o build/casc_extract.exe
