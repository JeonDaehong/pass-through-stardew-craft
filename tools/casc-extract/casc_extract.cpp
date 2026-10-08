// casc_extract: list or extract files from a StarCraft: Remastered CASC storage.
//
//   casc_extract list    <storage_dir> [mask]
//   casc_extract extract <storage_dir> <out_dir> <name> [name...]   ("-" reads names from stdin)
//
// Extracted files keep their storage path under out_dir, so OpenBW can load
// them through a plain directory loader instead of the original MPQs.

#include "CascLib.h"

#include <cstdio>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

namespace fs = std::filesystem;

static int list_files(HANDLE storage, const char* mask) {
	CASC_FIND_DATA fd;
	HANDLE find = CascFindFirstFile(storage, mask, &fd, nullptr);
	if (!find) {
		std::fprintf(stderr, "no files match '%s' (error %u)\n", mask, (unsigned)GetCascError());
		return 1;
	}
	size_t count = 0;
	do {
		std::printf("%s\t%llu\n", fd.szFileName, (unsigned long long)fd.FileSize);
		++count;
	} while (CascFindNextFile(find, &fd));
	CascFindClose(find);
	std::fprintf(stderr, "%zu files\n", count);
	return 0;
}

static bool extract_file(HANDLE storage, const std::string& name, const fs::path& out_dir) {
	HANDLE file = nullptr;
	if (!CascOpenFile(storage, name.c_str(), 0, CASC_OPEN_BY_NAME, &file)) {
		std::fprintf(stderr, "open failed: %s (error %u)\n", name.c_str(), (unsigned)GetCascError());
		return false;
	}
	ULONGLONG size = 0;
	CascGetFileSize64(file, &size);
	std::vector<char> data((size_t)size);
	DWORD read = 0;
	bool ok = CascReadFile(file, data.data(), (DWORD)size, &read) && read == size;
	CascCloseFile(file);
	if (!ok) {
		std::fprintf(stderr, "read failed: %s\n", name.c_str());
		return false;
	}

	// Storage names may carry a "locale:" or "data:" prefix; strip it for the output path.
	std::string rel = name;
	if (auto colon = rel.find(':'); colon != std::string::npos) rel = rel.substr(colon + 1);
	fs::path dst = out_dir / fs::path(rel).relative_path();
	fs::create_directories(dst.parent_path());
	std::ofstream(dst, std::ios::binary).write(data.data(), (std::streamsize)data.size());
	std::printf("%s -> %s (%llu bytes)\n", name.c_str(), dst.string().c_str(), (unsigned long long)size);
	return true;
}

int main(int argc, char** argv) {
	if (argc < 3) {
		std::fprintf(stderr, "usage:\n  %s list <storage_dir> [mask]\n  %s extract <storage_dir> <out_dir> <name>...\n", argv[0], argv[0]);
		return 2;
	}
	std::string cmd = argv[1];

	HANDLE storage = nullptr;
	if (!CascOpenStorage(argv[2], 0, &storage)) {
		std::fprintf(stderr, "cannot open storage %s (error %u)\n", argv[2], (unsigned)GetCascError());
		return 1;
	}

	int rc = 2;
	if (cmd == "list") {
		rc = list_files(storage, argc > 3 ? argv[3] : "*");
	} else if (cmd == "extract" && argc > 4) {
		rc = 0;
		for (int i = 4; i < argc; ++i) {
			if (std::string(argv[i]) == "-") {
				// Read one name per line from stdin; avoids the command line length limit.
				std::string line;
				char buf[MAX_PATH * 2];
				while (std::fgets(buf, sizeof(buf), stdin)) {
					line = buf;
					while (!line.empty() && (line.back() == '\n' || line.back() == '\r')) line.pop_back();
					if (!line.empty() && !extract_file(storage, line, argv[3])) rc = 1;
				}
			} else if (!extract_file(storage, argv[i], argv[3])) {
				rc = 1;
			}
		}
	}
	CascCloseStorage(storage);
	return rc;
}
