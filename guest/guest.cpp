// StardewCraft guest: runs OpenBW headless and exchanges state with the
// Stardew Valley host through shared memory (see docs/DESIGN.md).
//
//   stardewcraft_guest <data_dir> <base_map.scm> [shm_name]
//
// <data_dir> holds files extracted from StarCraft: Remastered (tools/casc-extract).
// <base_map.scm> only donates the scenario.chk chunks we do not generate.

#include "shm.h"

#include "bwgame.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <fstream>
#include <iterator>
#include <memory>
#include <thread>

using namespace bwgame;

namespace {

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

// ---- shared memory -------------------------------------------------------

struct shm_view {
	HANDLE mapping = nullptr;
	uint8_t* base = nullptr;

	bool open(const wchar_t* name) {
		mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sdc::shm_size, name);
		if (!mapping) return false;
		base = (uint8_t*)MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sdc::shm_size);
		return base != nullptr;
	}
	template<typename T> T& at(size_t offset) { return *(T*)(base + offset); }
	sdc::header_t& header() { return at<sdc::header_t>(sdc::off_header); }
	volatile uint32_t& u32(size_t offset) { return *(volatile uint32_t*)(base + offset); }
};

bool read_proxies(shm_view& shm, sdc::proxy_table_t& out) {
	auto& table = shm.at<sdc::proxy_table_t>(sdc::off_proxies);
	volatile uint32_t& seq = *(volatile uint32_t*)&table.seq;
	for (int tries = 0; tries != 8; ++tries) {
		uint32_t s0 = seq;
		if (s0 & 1) continue;
		std::atomic_thread_fence(std::memory_order_acquire);
		out.count = std::min(table.count, sdc::proxy_capacity);
		std::memcpy(out.proxies, table.proxies, out.count * sizeof(sdc::proxy_entry_t));
		std::atomic_thread_fence(std::memory_order_acquire);
		if (seq == s0) return true;
	}
	return false;
}

bool process_alive(uint32_t pid) {
	if (!pid) return true; // host not attached yet (standalone testing)
	HANDLE h = OpenProcess(SYNCHRONIZE, FALSE, pid);
	if (!h) return false;
	bool alive = WaitForSingleObject(h, 0) == WAIT_TIMEOUT;
	CloseHandle(h);
	return alive;
}

// ---- map generation ------------------------------------------------------

struct chunk_t {
	std::array<char, 4> tag;
	a_vector<uint8_t> data;
};

a_vector<chunk_t> parse_chunks(const a_vector<uint8_t>& chk) {
	a_vector<chunk_t> r;
	size_t pos = 0;
	while (pos + 8 <= chk.size()) {
		chunk_t c;
		std::memcpy(c.tag.data(), &chk[pos], 4);
		int32_t len;
		std::memcpy(&len, &chk[pos + 4], 4);
		pos += 8;
		if (len < 0 || pos + (size_t)len > chk.size()) break;
		c.data.assign(chk.begin() + pos, chk.begin() + pos + len);
		pos += len;
		r.push_back(std::move(c));
	}
	return r;
}

// Finds one tile group whose megatiles are entirely low-ground walkable and one
// that is entirely unwalkable, for the given tileset.
std::pair<uint16_t, uint16_t> pick_tiles(const global_state& global, size_t tileset) {
	auto& cv5 = global.tileset_cv5.at(tileset);
	auto& vf4 = global.tileset_vf4.at(tileset);
	auto megatile_class = [&](size_t mega) {
		// 0 = mixed, 1 = all walkable low ground, 2 = all unwalkable
		if ((mega + 1) * 32 > vf4.size()) return 0;
		bool all_walk = true, none_walk = true;
		for (size_t i = 0; i != 16; ++i) {
			uint16_t f = vf4[mega * 32 + i * 2] | (vf4[mega * 32 + i * 2 + 1] << 8);
			bool walk = f & 1;
			bool low = (f & 6) == 0;
			if (!walk || !low) all_walk = false;
			if (walk) none_walk = false;
		}
		return all_walk ? 1 : none_walk ? 2 : 0;
	};
	int walkable = -1, blocked = -1;
	for (size_t group = 1; group < cv5.size() / 52; ++group) {
		const uint8_t* e = &cv5[group * 52];
		uint16_t flags = e[2] | (e[3] << 8);
		size_t mega = e[20] | (e[21] << 8);
		if (!mega) continue;
		int cls = megatile_class(mega);
		// Walkable ground must also be buildable (and not creep) so farm turrets can go anywhere.
		bool buildable = !(flags & (tile_t::flag_unbuildable | tile_t::flag_has_creep));
		if (cls == 1 && walkable < 0 && buildable) walkable = (int)group;
		if (cls == 2 && blocked < 0) blocked = (int)group;
		if (walkable >= 0 && blocked >= 0) break;
	}
	if (walkable < 0 || blocked < 0) error("tileset %d has no usable walkable/blocked tile groups", (int)tileset);
	return {uint16_t(walkable * 16), uint16_t(blocked * 16)};
}

a_vector<uint8_t> build_chk(const a_vector<uint8_t>& base_chk, const global_state& global,
                            int width, int height, const uint8_t* grid) {
	auto chunks = parse_chunks(base_chk);
	size_t tileset = 0;
	for (auto& c : chunks) {
		if (!std::memcmp(c.tag.data(), "ERA ", 4) && c.data.size() >= 2) tileset = (c.data[0] | (c.data[1] << 8)) % 8;
	}
	auto [walk_tile, block_tile] = pick_tiles(global, tileset);

	auto u16le = [](a_vector<uint8_t>& v, uint16_t x) { v.push_back(x & 0xff); v.push_back(x >> 8); };

	a_vector<uint8_t> dim;
	u16le(dim, (uint16_t)width);
	u16le(dim, (uint16_t)height);

	a_vector<uint8_t> mtxm;
	for (int y = 0; y != height; ++y) {
		for (int x = 0; x != width; ++x) u16le(mtxm, grid[y * width + x] ? walk_tile : block_tile);
	}
	a_vector<uint8_t> mask((size_t)width * height, 0xff);

	// Player 0 is the farmer's side; 1-3 are the zerg, protoss and terran raiders.
	a_vector<uint8_t> ownr(12, 7);
	for (int i = 0; i != 4; ++i) ownr[i] = 6;
	a_vector<uint8_t> side = {1, 0, 2, 1, 5, 5, 5, 5, 7, 7, 7, 4};

	auto replacement = [&](const char* tag) -> const a_vector<uint8_t>* {
		static const a_vector<uint8_t> empty;
		if (!std::memcmp(tag, "DIM ", 4)) return &dim;
		if (!std::memcmp(tag, "MTXM", 4) || !std::memcmp(tag, "TILE", 4)) return &mtxm;
		if (!std::memcmp(tag, "MASK", 4)) return &mask;
		if (!std::memcmp(tag, "OWNR", 4) || !std::memcmp(tag, "IOWN", 4)) return &ownr;
		if (!std::memcmp(tag, "SIDE", 4)) return &side;
		if (!std::memcmp(tag, "UNIT", 4) || !std::memcmp(tag, "THG2", 4) || !std::memcmp(tag, "DD2 ", 4) ||
		    !std::memcmp(tag, "TRIG", 4) || !std::memcmp(tag, "ISOM", 4)) return &empty;
		return nullptr;
	};

	a_vector<uint8_t> out;
	a_vector<std::array<char, 4>> written;
	for (auto& c : chunks) {
		auto* rep = replacement(c.tag.data());
		if (rep && std::find(written.begin(), written.end(), c.tag) != written.end()) continue;
		auto& data = rep ? *rep : c.data;
		out.insert(out.end(), c.tag.begin(), c.tag.end());
		uint32_t len = (uint32_t)data.size();
		for (int i = 0; i != 4; ++i) out.push_back((len >> (i * 8)) & 0xff);
		out.insert(out.end(), data.begin(), data.end());
		if (rep) written.push_back(c.tag);
	}
	return out;
}

// ---- simulation ----------------------------------------------------------

constexpr int farmer_owner = 0;
constexpr int first_raider = 1;
constexpr int last_raider = 3;

bool is_raider(int owner) { return owner >= first_raider && owner <= last_raider; }

struct world {
	std::unique_ptr<game_state> game_st;
	std::unique_ptr<state> st;
	optional<state_functions> funcs;

	struct proxy_t {
		unit_t* u = nullptr;
		uint32_t damage_total = 0;
		bool seen = false;
	};
	a_unordered_map<uint32_t, proxy_t> proxies; // by host proxy id; 0 is the farmer
	a_unordered_set<const unit_t*> proxy_units;
	a_unordered_map<uint32_t, uint32_t> held_until; // unit index -> frame its host order expires

	uint32_t map_id = 0;
	uint32_t frame = 0;

	void load(global_state& global, const a_vector<uint8_t>& chk, uint32_t id) {
		funcs.reset();
		game_st = std::make_unique<game_state>();
		st = std::make_unique<state>();
		st->global = &global;
		st->game = game_st.get();
		a_vector<uint8_t> data = chk;
		game_load_functions(*st).load_map_data(data.data(), data.size());
		funcs.emplace(*st);
		proxies.clear();
		proxy_units.clear();
		held_until.clear();
		map_id = id;
		frame = 0;

		// The farm is known ground: BW refuses to place buildings on unexplored tiles
		// (an explored bit set means "not yet explored").
		for (auto& tile : st->tiles) tile.explored = 0;

		// Raiders are allied with each other and hostile to the farmer's side.
		for (int a = 0; a != 12; ++a) {
			for (int b = 0; b != 12; ++b) {
				st->alliances[a][b] = a == b || (is_raider(a) && is_raider(b)) ? 2 : 0;
			}
		}
	}
	bool loaded() const { return (bool)funcs; }
	state_functions& f() { return *funcs; }

	unit_t* farmer() {
		auto it = proxies.find(0);
		return it != proxies.end() ? it->second.u : nullptr;
	}
	bool is_proxy(const unit_t* u) const { return proxy_units.count(u) != 0; }

	// Returns how many units could not be created.
	int spawn(int unit_type, int count, int owner, xy pos) {
		if (unit_type < 0 || unit_type >= (int)UnitTypes::None) return count;
		auto* type = f().get_unit_type((UnitTypes)unit_type);
		int failures = 0;
		for (int i = 0; i != count; ++i) {
			xy p = pos;
			if (f().ut_building(type)) {
				// Snap to the BW build grid like the map editor does.
				xy top_left = pos - type->placement_size / 2;
				p = top_left / 32 * 32 + type->placement_size / 2;
				if (!f().can_place_building(nullptr, owner, type, p, true, false)) {
					size_t ti = (size_t)(top_left.y / 32) * f().game_st.map_tile_width + top_left.x / 32;
					auto& t = f().st.tiles.at(ti);
					std::fprintf(stderr, "cannot place unit type %d at (%d,%d): tile flags %x explored %x visible %x\n",
						unit_type, p.x, p.y, (unsigned)t.flags, (unsigned)t.explored, (unsigned)t.visible);
					++failures;
					continue;
				}
			} else if (count > 1) {
				int spacing = std::max(type->dimensions.from.x + type->dimensions.to.x, 16) + 4;
				p += xy{(i % 4) * spacing - spacing * 3 / 2, (i / 4) * spacing};
			}
			if (!f().create_completed_unit(type, p, owner)) ++failures;
		}
		return failures;
	}

	// Puts back one remembered unit exactly where it was, with its remaining hit points.
	bool spawn_unit(int unit_type, int owner, int hp_percent, xy pos) {
		if (unit_type < 0 || unit_type >= (int)UnitTypes::None) return false;
		unit_t* u = f().create_completed_unit(f().get_unit_type((UnitTypes)unit_type), pos, owner);
		if (!u) return false;
		if (hp_percent > 0 && hp_percent < 100) {
			fp8 max_hp = u->unit_type->hitpoints;
			u->hp = std::max(1_fp8, max_hp * hp_percent / 100);
		}
		return true;
	}

	void clear_raiders() {
		a_vector<unit_t*> doomed;
		for (unit_t* u : ptr(f().st.visible_units)) if (is_raider(u->owner)) doomed.push_back(u);
		for (unit_t* u : doomed) f().kill_unit(u);
	}

	unit_t* find_unit(uint32_t index) {
		for (unit_t* u : ptr(f().st.visible_units)) if ((uint32_t)u->index == index) return u;
		return nullptr;
	}

	// Damage from the farmer's weapons: shields soak it first, then hit points.
	void damage(uint32_t index, int amount) {
		unit_t* u = find_unit(index);
		if (!u || is_proxy(u) || amount <= 0 || f().u_invincible(u)) return;
		fp8 dmg = fp8::integer(amount);
		if (u->unit_type->has_shield && u->shield_points > 0_fp8) {
			fp8 absorbed = std::min(u->shield_points, dmg);
			f().set_unit_shield_points(u, u->shield_points - absorbed);
			dmg -= absorbed;
		}
		if (dmg > 0_fp8) f().unit_deal_damage(u, dmg, nullptr, farmer_owner);
	}

	// Mirrors the farmer and animals as invisible civilians that raiders can hunt.
	// They never die: lost hit points are refilled and reported as damage instead.
	void sync_proxies(const sdc::proxy_table_t& table) {
		a_unordered_set<const unit_t*> alive;
		for (unit_t* u : ptr(f().st.visible_units)) alive.insert(u);

		for (auto& v : proxies) v.second.seen = false;
		auto* civilian = f().get_unit_type(UnitTypes::Terran_Civilian);
		for (uint32_t i = 0; i != table.count; ++i) {
			auto& e = table.proxies[i];
			auto& p = proxies[e.id];
			p.seen = true;
			xy pos{e.x, e.y};
			if (!p.u || !alive.count(p.u)) {
				p.u = f().create_completed_unit(civilian, pos, farmer_owner);
				if (!p.u) continue;
			}
			if (p.u->sprite->position != pos) f().move_unit(p.u, pos);
			fp8 max_hp = p.u->unit_type->hitpoints;
			if (p.u->hp < max_hp) {
				p.damage_total += (max_hp - p.u->hp).integer_part();
				p.u->hp = max_hp;
			}
		}
		for (auto it = proxies.begin(); it != proxies.end();) {
			if (!it->second.seen) {
				if (it->second.u && alive.count(it->second.u)) f().kill_unit(it->second.u);
				it = proxies.erase(it);
			} else {
				++it;
			}
		}
		proxy_units.clear();
		for (auto& v : proxies) if (v.second.u) proxy_units.insert(v.second.u);
	}

	void export_proxy_damage(sdc::proxy_damage_table_t& table) {
		uint32_t n = 0;
		for (auto& v : proxies) {
			if (n == sdc::proxy_capacity) break;
			table.entries[n++] = {v.first, v.second.damage_total};
		}
		table.count = n;
	}

	// Host-directed orders (e.g. "go eat that crop") override the default hunt for a while.
	void order(uint32_t index, Orders order_id, xy pos) {
		unit_t* u = find_unit(index);
		if (!u || !is_raider(u->owner) || u->order_type->id == Orders::Die) return;
		f().set_unit_order(u, f().get_order_type(order_id), pos);
		held_until[index] = frame + 24 * 10;
	}

	// Raiders attack-move towards the farmer, so they also fight any turret on the way.
	void command_raiders() {
		unit_t* target_unit = farmer();
		if (frame % 12 != 0 || !target_unit) return;
		bool refresh = frame % 48 == 0;
		xy target = target_unit->sprite->position;
		for (unit_t* u : ptr(f().st.visible_units)) {
			if (!is_raider(u->owner) || f().ut_turret(u) || f().ut_building(u)) continue;
			auto held = held_until.find((uint32_t)u->index);
			if (held != held_until.end()) {
				if (held->second > frame) continue;
				held_until.erase(held);
			}
			auto order = u->order_type->id;
			if (order == Orders::Die || order == Orders::AttackUnit) continue;
			bool engaged = order == Orders::AttackMove && u->order_target.unit;
			if (order == Orders::AttackMove && (engaged || !refresh)) continue;
			f().set_unit_order(u, f().get_order_type(Orders::AttackMove), target);
		}
	}

	void export_units(sdc::unit_table_t& table) {
		volatile uint32_t& seq = *(volatile uint32_t*)&table.seq;
		seq = seq + 1;
		std::atomic_thread_fence(std::memory_order_release);

		uint32_t n = 0;
		for (unit_t* u : ptr(f().st.visible_units)) {
			if (is_proxy(u) || f().ut_turret(u) || u->hp == 0_fp8 || n == sdc::unit_capacity) continue;
			auto& e = table.units[n++];
			e.id = (uint32_t)u->index;
			e.unit_type = (uint16_t)u->unit_type->id;
			e.owner = (uint8_t)u->owner;
			e.flags = (f().u_flying(u) ? 1 : 0) | (f().ut_building(u) ? 2 : 0);
			e.x = u->sprite->position.x;
			e.y = u->sprite->position.y;
			e.hp = u->hp.integer_part();
			e.max_hp = u->unit_type->hitpoints.integer_part();
			e.shields = u->unit_type->has_shield ? u->shield_points.integer_part() : 0;
			e.max_shields = u->unit_type->has_shield ? (int)u->unit_type->shield_points : 0;
		}
		table.count = n;
		table.map_id = map_id;
		table.frame = frame;

		std::atomic_thread_fence(std::memory_order_release);
		seq = seq + 1;
	}

	void step() {
		f().next_frame();
		++frame;
	}

	bool is_proxy_sprite(const sprite_t* s) const {
		for (auto& v : proxies) if (v.second.u && v.second.u->sprite == s) return true;
		return false;
	}

	void export_images(sdc::snapshot_t& snap) {
		volatile uint32_t& seq = *(volatile uint32_t*)&snap.seq;
		seq = seq + 1;
		std::atomic_thread_fence(std::memory_order_release);

		a_vector<std::pair<uint32_t, const sprite_t*>> sprites;
		for (auto& line : f().st.sprites_on_tile_line) {
			for (const sprite_t* s : ptr(line)) {
				if (f().s_hidden(s)) continue;
				if (s->owner == farmer_owner && is_proxy_sprite(s)) continue;
				uint32_t depth = (uint32_t)s->elevation_level << 14;
				if (s->elevation_level <= 4) depth |= (uint32_t)s->position.y << 1;
				sprites.emplace_back(depth, s);
			}
		}
		std::stable_sort(sprites.begin(), sprites.end(), [](auto& a, auto& b) { return a.first < b.first; });

		a_unordered_map<const sprite_t*, uint32_t> sprite_units;
		for (unit_t* u : ptr(f().st.visible_units)) sprite_units[u->sprite] = (uint32_t)u->index + 1;

		uint32_t n = 0;
		for (auto& v : sprites) {
			const sprite_t* s = v.second;
			auto unit_it = sprite_units.find(s);
			for (const image_t* img : ptr(reverse(s->images))) {
				if (f().i_flag(img, image_t::flag_hidden)) continue;
				if (n == sdc::image_capacity) break;
				xy p = f().get_image_map_position(img);
				auto& e = snap.images[n++];
				e.image_id = (uint16_t)img->image_type->id;
				e.frame = (uint16_t)img->frame_index;
				e.x = p.x;
				e.y = p.y;
				e.flags = f().i_flag(img, image_t::flag_horizontally_flipped) ? 1 : 0;
				e.modifier = (uint8_t)img->modifier;
				e.color = (uint8_t)f().st.players[s->owner].color;
				e.reserved0 = 0;
				e.unit_id = unit_it != sprite_units.end() ? unit_it->second : 0;
				e.reserved1 = 0;
			}
		}
		snap.count = n;
		snap.map_id = map_id;
		snap.frame = frame;

		std::atomic_thread_fence(std::memory_order_release);
		seq = seq + 1;
	}
};

} // namespace

int main(int argc, char** argv) {
	if (argc < 3) {
		std::fprintf(stderr, "usage: %s <data_dir> <base_map.scm>\n", argv[0]);
		return 2;
	}

	// The host reads our output through a pipe; don't let it sit in a buffer.
	std::setvbuf(stdout, nullptr, _IONBF, 0);

	// An alternate name lets tools/fake-host run next to a live game.
	std::wstring name = sdc::shm_name;
	if (argc > 3) name.assign(argv[3], argv[3] + std::strlen(argv[3]));

	shm_view shm;
	if (!shm.open(name.c_str())) {
		std::fprintf(stderr, "cannot open shared memory (error %lu)\n", GetLastError());
		return 1;
	}
	auto& hdr = shm.header();
	if (hdr.magic == 0) {
		hdr.magic = sdc::magic;
		hdr.version = sdc::version;
	}
	if (hdr.magic != sdc::magic || hdr.version != sdc::version) {
		std::fprintf(stderr, "shared memory version mismatch (magic %08x version %u)\n", hdr.magic, hdr.version);
		return 1;
	}
	hdr.guest_pid = GetCurrentProcessId();
	hdr.guest_status = sdc::status_starting;

	try {
		auto global = std::make_unique<global_state>();
		global_init(*global, directory_loader{argv[1]});

		a_vector<uint8_t> base_chk;
		data_loading::mpq_file<> base_map{a_string(argv[2])};
		base_map(base_chk, "staredit/scenario.chk");
		std::printf("guest ready (pid %lu)\n", GetCurrentProcessId());
		hdr.guest_status = sdc::status_idle;

		world w;
		auto& snap = shm.at<sdc::snapshot_t>(sdc::off_snapshot);
		auto& units = shm.at<sdc::unit_table_t>(sdc::off_units);
		auto& proxy_damage = shm.at<sdc::proxy_damage_table_t>(sdc::off_proxy_damage);
		sdc::proxy_table_t proxy_table{};

		// BW "fastest" speed: 42 ms per game frame.
		auto next_tick = std::chrono::steady_clock::now();
		while (true) {
			next_tick += std::chrono::milliseconds(42);
			hdr.guest_heartbeat = hdr.guest_heartbeat + 1;
			if (!process_alive(hdr.host_pid)) {
				std::printf("host exited, shutting down\n");
				break;
			}

			uint32_t head = shm.u32(sdc::off_cmd_head);
			uint32_t tail = shm.u32(sdc::off_cmd_tail);
			std::atomic_thread_fence(std::memory_order_acquire);
			while (tail != head) {
				auto cmd = shm.at<sdc::command_t>(sdc::off_commands + (tail % sdc::command_capacity) * sizeof(sdc::command_t));
				if (cmd.type == sdc::cmd_load_map) {
					int width = std::clamp(cmd.a, 1, (int)sdc::grid_max);
					int height = std::clamp(cmd.b, 1, (int)sdc::grid_max);
					auto chk = build_chk(base_chk, *global, width, height, shm.base + sdc::off_grid);
					w.load(*global, chk, (uint32_t)cmd.c);
					hdr.map_id = (uint32_t)cmd.c;
					std::printf("loaded map %d: %dx%d\n", cmd.c, width, height);
				} else if (cmd.type == sdc::cmd_spawn && w.loaded()) {
					uint32_t a = (uint32_t)cmd.a;
					int failures = w.spawn(a & 0xffff, (a >> 16) & 0xff, a >> 24, xy{cmd.b, cmd.c});
					hdr.spawn_failures = hdr.spawn_failures + failures;
				} else if (cmd.type == sdc::cmd_spawn_unit && w.loaded()) {
					uint32_t a = (uint32_t)cmd.a;
					if (!w.spawn_unit(a & 0xffff, (a >> 16) & 0xff, a >> 24, xy{cmd.b, cmd.c})) {
						hdr.spawn_failures = hdr.spawn_failures + 1;
					}
				} else if (cmd.type == sdc::cmd_clear && w.loaded()) {
					w.clear_raiders();
				} else if (cmd.type == sdc::cmd_damage && w.loaded()) {
					w.damage((uint32_t)cmd.a, cmd.b);
				} else if (cmd.type == sdc::cmd_move_to && w.loaded()) {
					w.order((uint32_t)cmd.a, Orders::Move, xy{cmd.b, cmd.c});
				} else if (cmd.type == sdc::cmd_attack_move_to && w.loaded()) {
					w.order((uint32_t)cmd.a, Orders::AttackMove, xy{cmd.b, cmd.c});
				}
				++tail;
			}
			std::atomic_thread_fence(std::memory_order_release);
			shm.u32(sdc::off_cmd_tail) = tail;

			if (w.loaded()) {
				if (read_proxies(shm, proxy_table)) w.sync_proxies(proxy_table);
				w.command_raiders();
				w.step();
				w.export_images(snap);
				w.export_units(units);
				w.export_proxy_damage(proxy_damage);
				auto farmer_proxy = w.proxies.find(0);
				if (farmer_proxy != w.proxies.end()) hdr.farmer_damage_total = farmer_proxy->second.damage_total;
				hdr.guest_frame = w.frame;
				hdr.guest_status = sdc::status_running;
			}

			std::this_thread::sleep_until(next_tick);
			if (std::chrono::steady_clock::now() > next_tick + std::chrono::milliseconds(500)) {
				next_tick = std::chrono::steady_clock::now(); // don't try to catch up after a stall
			}
		}
	} catch (const std::exception& e) {
		std::fprintf(stderr, "guest error: %s\n", e.what());
		hdr.guest_status = sdc::status_error;
		return 1;
	}
	return 0;
}
