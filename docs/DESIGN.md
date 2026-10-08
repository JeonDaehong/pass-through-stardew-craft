# StardewCraft design

A SkyCraft-style passthrough mod: Stardew Valley is the **host** (world, rendering,
the farmer), a headless OpenBW process is the **guest** (StarCraft units, pathing,
combat). Both run on the same machine and talk through one named shared-memory block.

## Authority

| Concern | Owner | Notes |
|---|---|---|
| Location layout / collision | Stardew | Sent as a walkability grid; guest builds a BW map from it |
| Farmer, animal and villager positions | Stardew | Guest mirrors each as an invisible, unkillable Terran Civilian ("proxy") |
| Crops | Stardew | Host sends some ground raiders to crop tiles (MOVE_TO) and destroys crops they stand on |
| Units, orders, pathing, combat, animation | OpenBW | Host only draws what the guest reports |
| Damage to proxies | OpenBW → Stardew | Guest refills proxy HP and reports cumulative damage per proxy; host hurts the farmer, makes animals flee or headbutt, villagers swing swords |

## Coordinates

One Stardew tile (64 world px) is one BW tile (32 px), so **BW px = Stardew world px / 2**.
Sprites are drawn at 2x scale so a BW tile covers exactly one Stardew tile.

## Shared memory `Local\StardewCraft_v1` (protocol version 4)

128 KiB, little-endian, fixed offsets. Whoever opens it first creates it; the host
writes `magic`/`version` and both sides refuse to run on a mismatch.

### Header @ 0x0000 (64 bytes)
| Off | Type | Field | Writer |
|---|---|---|---|
| 0 | u32 | magic `0x52434453` ("SDCR") | host |
| 4 | u32 | version = 4 | host |
| 8 | u32 | host_pid | host |
| 12 | u32 | guest_pid | guest |
| 16 | u32 | host_heartbeat | host |
| 20 | u32 | guest_heartbeat | guest |
| 24 | u32 | guest_frame (game frames since map load) | guest |
| 28 | u32 | guest_status: 0 starting, 1 idle (no map), 2 running, 3 error | guest |
| 32 | u32 | farmer_damage_total (monotonic) | guest |
| 36 | u32 | map_id currently simulated | guest |
| 40 | u32 | spawn_failures (monotonic; host refunds turrets that failed) | guest |

### Proxy table @ 0x0500 (seqlock, host writes)
`u32 seq, u32 count`, then up to 64 entries of `u32 id, i32 x, i32 y, u32 kind`
(BW px; kind 0 farmer, 1 small animal, 2 large animal, 3 villager). Id 0 is always the farmer.
Proxies missing from the table are removed by the guest.

### Proxy damage @ 0x0A00 (guest writes)
`u32 count, u32 reserved`, then `u32 id, u32 damage_total` per live proxy (monotonic).

### Command ring @ 0x0080 (host → guest)
`u32 head` (host advances) @ 0x80, `u32 tail` (guest advances) @ 0x84,
64 entries of 16 bytes from 0x88: `u32 type, i32 a, i32 b, i32 c`.

| type | name | a | b | c |
|---|---|---|---|---|
| 1 | LOAD_MAP | width tiles | height tiles | map_id |
| 2 | SPAWN | `unit_type \| count << 16 \| owner << 24` | x (BW px) | y (BW px) |
| 3 | CLEAR (kills raiders only) | – | – | – |
| 4 | DAMAGE | unit id | amount (shields first) | – |
| 5 | MOVE_TO | unit id | x (BW px) | y (BW px) |
| 6 | ATTACK_MOVE_TO | unit id | x (BW px) | y (BW px) |
| 7 | SPAWN_UNIT | `unit_type \| owner << 16 \| hp_percent << 24` | x (BW px) | y (BW px) |

MOVE_TO / ATTACK_MOVE_TO only apply to raiders and hold for 240 frames (10 s), after which
the guest resumes its default attack-move towards the farmer.

LOAD_MAP reads the walkability grid, which the host must fill **before** publishing the command.

### Walkability grid @ 0x1000
`width * height` bytes, row-major, `1` = walkable. Max 256 × 256.

### Image snapshot @ 0x12000 (seqlock, guest writes)
`u32 seq, u32 count, u32 map_id, u32 frame`, then up to 1024 entries of 24 bytes,
already in back-to-front draw order:

| Off | Type | Field |
|---|---|---|
| 0 | u16 | image id (index into `arr/images.dat`) |
| 2 | u16 | GRP frame |
| 4 | i32 | x — frame top-left, BW px |
| 8 | i32 | y — frame top-left, BW px |
| 12 | u8 | flags: 1 = horizontally flipped |
| 13 | u8 | modifier (BW draw function: 0/1 normal, 10 shadow, ...) |
| 14 | u8 | player color index |
| 15 | u8 | reserved |
| 16 | u32 | unit id + 1 if the sprite belongs to a unit, else 0 |
| 20 | u32 | reserved |

Seqlock rule: writer makes `seq` odd, writes, makes it even. Reader retries when
`seq` is odd or changed during the copy.

### Unit table @ 0x19000 (seqlock, guest writes)
`u32 seq, u32 count, u32 map_id, u32 frame`, then up to 512 entries of 32 bytes:
`u32 id, u16 unit_type, u8 owner, u8 flags (1 flying, 2 building), i32 x, i32 y (center, BW px),
i32 hp, i32 max_hp, i32 shields, i32 max_shields`. The proxy and turret subunits are omitted.

### Players
| Owner | Side |
|---|---|
| 0 | Farmer: proxy civilian and crop-bought defences |
| 1 / 2 / 3 | Zerg / Protoss / Terran raiders, allied with each other |

## Lifecycle
- The host launches the guest on `GameLaunched` and kills it on exit.
- The guest exits by itself when `host_pid` is no longer running.
- On every warp the host sends LOAD_MAP for the new location, then respawns that location's
  remembered defences (saved with the game) and raiders (SPAWN_UNIT, kept in memory until the next morning).

## Data
The guest and host read StarCraft data extracted from the user's own Remastered
install (`tools/casc-extract`) into `data/bw/`. That directory is Blizzard content
and must never be committed or shipped.
