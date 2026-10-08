// Shared memory layout for StardewCraft. Mirrors docs/DESIGN.md; keep
// host/StardewCraft/Shm.cs in sync with every change here.
#pragma once

#include <cstddef>
#include <cstdint>

namespace sdc {

constexpr const wchar_t* shm_name = L"Local\\StardewCraft_v1";
constexpr uint32_t shm_size = 0x20000;
constexpr uint32_t magic = 0x52434453; // "SDCR"
constexpr uint32_t version = 4;

enum guest_status : uint32_t { status_starting = 0, status_idle = 1, status_running = 2, status_error = 3 };
enum command_type : uint32_t { cmd_load_map = 1, cmd_spawn = 2, cmd_clear = 3, cmd_damage = 4, cmd_move_to = 5, cmd_attack_move_to = 6, cmd_spawn_unit = 7 };

struct header_t {
	uint32_t magic;
	uint32_t version;
	uint32_t host_pid;
	uint32_t guest_pid;
	uint32_t host_heartbeat;
	uint32_t guest_heartbeat;
	uint32_t guest_frame;
	uint32_t guest_status;
	uint32_t farmer_damage_total;
	uint32_t map_id;
	uint32_t spawn_failures;
	uint32_t reserved[5];
};
static_assert(sizeof(header_t) == 64, "header size");

// Things the host owns that raiders can attack: the farmer (id 0), farm animals and villagers.
// The guest mirrors each one as an invisible, unkillable civilian.
struct proxy_entry_t {
	uint32_t id;
	int32_t x; // BW px
	int32_t y;
	uint32_t kind; // 0 farmer, 1 small animal, 2 large animal, 3 villager
};

constexpr uint32_t proxy_capacity = 64;

struct proxy_table_t {
	uint32_t seq;
	uint32_t count;
	proxy_entry_t proxies[proxy_capacity];
};

struct proxy_damage_entry_t {
	uint32_t id;
	uint32_t damage_total; // monotonic per proxy id
};

struct proxy_damage_table_t {
	uint32_t count;
	uint32_t reserved;
	proxy_damage_entry_t entries[proxy_capacity];
};

struct command_t {
	uint32_t type;
	int32_t a;
	int32_t b;
	int32_t c;
};

constexpr uint32_t command_capacity = 64;

struct image_entry_t {
	uint16_t image_id;
	uint16_t frame;
	int32_t x;
	int32_t y;
	uint8_t flags;
	uint8_t modifier;
	uint8_t color;
	uint8_t reserved0;
	uint32_t unit_id;
	uint32_t reserved1;
};
static_assert(sizeof(image_entry_t) == 24, "image entry size");

constexpr uint32_t image_capacity = 1024;

struct snapshot_t {
	uint32_t seq;
	uint32_t count;
	uint32_t map_id;
	uint32_t frame;
	image_entry_t images[image_capacity];
};

// Live units, for host-side hit tests, health bars and turret persistence.
struct unit_entry_t {
	uint32_t id; // unit index; DAMAGE takes this value
	uint16_t unit_type;
	uint8_t owner;
	uint8_t flags; // 1 = flying, 2 = building
	int32_t x; // center, BW px
	int32_t y;
	int32_t hp;
	int32_t max_hp;
	int32_t shields;
	int32_t max_shields;
};
static_assert(sizeof(unit_entry_t) == 32, "unit entry size");

constexpr uint32_t unit_capacity = 512;

struct unit_table_t {
	uint32_t seq;
	uint32_t count;
	uint32_t map_id;
	uint32_t frame;
	unit_entry_t units[unit_capacity];
};

constexpr size_t off_header = 0x0;
constexpr size_t off_cmd_head = 0x80;
constexpr size_t off_cmd_tail = 0x84;
constexpr size_t off_commands = 0x88;
constexpr size_t off_proxies = 0x500;
constexpr size_t off_proxy_damage = 0xA00;
constexpr size_t off_grid = 0x1000;
constexpr size_t grid_max = 256;
constexpr size_t off_snapshot = 0x12000;
static_assert(off_commands + command_capacity * sizeof(command_t) <= off_proxies, "commands overlap proxies");
static_assert(off_proxies + sizeof(proxy_table_t) <= off_proxy_damage, "proxies overlap damage table");
static_assert(off_proxy_damage + sizeof(proxy_damage_table_t) <= off_grid, "damage table overlaps grid");
static_assert(off_grid + grid_max * grid_max <= off_snapshot, "grid overlaps snapshot");
constexpr size_t off_units = 0x19000;
static_assert(off_snapshot + sizeof(snapshot_t) <= off_units, "snapshot overlaps units");
static_assert(off_units + sizeof(unit_table_t) <= shm_size, "unit table exceeds shm");

}
