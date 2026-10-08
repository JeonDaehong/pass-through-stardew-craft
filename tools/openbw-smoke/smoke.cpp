// openbw-smoke: proves OpenBW can run headless on data extracted from
// StarCraft: Remastered (see tools/casc-extract) instead of the 1.16.1 MPQs.
//
//   openbw_smoke <data_dir> <map.scm>
//
// Spawns marines and zerglings, orders the zerglings to attack-move, runs the
// simulation for 30 game seconds and prints the survivors.

#include "bwgame.h"

#include <cstdio>
#include <fstream>
#include <iterator>

using namespace bwgame;

// Reads data files from a plain directory tree, e.g. <dir>/arr/units.dat.
struct directory_loader {
	a_string dir;
	void operator()(a_vector<uint8_t>& dst, a_string filename) {
		for (auto& c : filename) if (c == '\\') c = '/';
		a_string path = dir + "/" + filename;
		std::ifstream f(path.c_str(), std::ios::binary);
		if (!f) error("directory_loader: %s: file not found", path);
		dst.assign(std::istreambuf_iterator<char>(f), std::istreambuf_iterator<char>());
	}
};

static int count_units(state_functions& funcs, int owner, UnitTypes type) {
	int n = 0;
	for (unit_t* u : ptr(funcs.st.visible_units)) {
		if (u->owner == owner && u->unit_type->id == type) ++n;
	}
	return n;
}

int main(int argc, char** argv) {
	if (argc < 3) {
		std::fprintf(stderr, "usage: %s <data_dir> <map.scm>\n", argv[0]);
		return 2;
	}
	try {
		game_player player(directory_loader{argv[1]});
		std::printf("global data loaded\n");

		player.load_map_file(argv[2]);
		auto& funcs = player.funcs();
		std::printf("map loaded: %dx%d px, tileset %d\n",
			(int)funcs.game_st.map_width, (int)funcs.game_st.map_height, (int)funcs.game_st.tileset_index);

		// A start location is guaranteed to be open, walkable ground.
		xy center = funcs.game_st.start_locations[0];
		std::printf("battle at start location (%d,%d), walkable=%d\n", center.x, center.y, (int)funcs.is_walkable(center));
		for (int i = 0; i != 4; ++i) {
			funcs.create_completed_unit(funcs.get_unit_type(UnitTypes::Terran_Marine), center + xy{i * 20, 0}, 0);
		}
		for (int i = 0; i != 6; ++i) {
			unit_t* z = funcs.create_completed_unit(funcs.get_unit_type(UnitTypes::Zerg_Zergling), center + xy{i * 16, 96}, 1);
			if (z) funcs.set_unit_order(z, funcs.get_order_type(Orders::AttackMove), center);
		}
		std::printf("spawned: marines=%d zerglings=%d\n",
			count_units(funcs, 0, UnitTypes::Terran_Marine), count_units(funcs, 1, UnitTypes::Zerg_Zergling));

		// 24 frames per game second on "fastest".
		for (int frame = 1; frame <= 24 * 30; ++frame) {
			player.next_frame();
			if (frame % 120 == 0) {
				std::printf("t=%2ds marines=%d zerglings=%d\n", frame / 24,
					count_units(funcs, 0, UnitTypes::Terran_Marine), count_units(funcs, 1, UnitTypes::Zerg_Zergling));
			}
		}
		for (unit_t* u : ptr(funcs.st.visible_units)) {
			bool marine = u->unit_type->id == UnitTypes::Terran_Marine;
			if (!marine && u->unit_type->id != UnitTypes::Zerg_Zergling) continue;
			std::printf("  owner %d %-8s at (%d,%d) hp %d\n", u->owner, marine ? "marine" : "zergling",
				u->sprite->position.x, u->sprite->position.y, u->hp.integer_part());
		}
	} catch (const std::exception& e) {
		std::fprintf(stderr, "error: %s\n", e.what());
		return 1;
	}
	return 0;
}
