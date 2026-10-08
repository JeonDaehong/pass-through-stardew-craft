"""Stand-in for the Stardew host: drives the guest through shared memory.

    python tools/fake-host/fake_host.py [guest.exe]

Set STARCRAFT_DIR if StarCraft: Remastered isn't in the default install folder.

Uses its own shared memory name, so it can run next to a live game. Loads an
80x65 map with a wall, places a siege tank and a missile turret next to a
stationary farmer, sends a mixed raid, hits one raider with "weapon" damage and
reports the unit table every second.
"""

import mmap
import os
import struct
import subprocess
import sys
import time
from collections import Counter

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
GUEST = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else os.path.join(ROOT, "build", "stardewcraft_guest.exe")
DATA = os.path.join(ROOT, "data", "bw")
STARCRAFT_DIR = os.environ.get("STARCRAFT_DIR", r"C:\Program Files (x86)\StarCraft")
BASE_MAP = os.path.join(STARCRAFT_DIR, "Maps", "(2)Bottleneck.scm")

SHM_NAME = "Local\\StardewCraft_test"
SHM_SIZE = 0x20000
MAGIC, VERSION = 0x52434453, 4  # keep in sync with guest/shm.h
OFF_HEAD, OFF_CMDS, OFF_PROXIES, OFF_PDMG, OFF_GRID, OFF_UNITS = 0x80, 0x88, 0x500, 0xA00, 0x1000, 0x19000
CMD_LOAD_MAP, CMD_SPAWN, CMD_CLEAR, CMD_DAMAGE, CMD_MOVE_TO = 1, 2, 3, 4, 5

NAMES = {0: "marine", 30: "tank(siege)", 32: "firebat", 37: "zergling", 38: "hydra", 43: "muta",
         65: "zealot", 66: "dragoon", 124: "missile turret"}


def main():
    shm = mmap.mmap(-1, SHM_SIZE, tagname=SHM_NAME)
    shm[:SHM_SIZE] = bytes(SHM_SIZE)
    struct.pack_into("<III", shm, 0, MAGIC, VERSION, os.getpid())

    head = 0

    def push(cmd, a, b, c):
        nonlocal head
        struct.pack_into("<Iiii", shm, OFF_CMDS + (head % 64) * 16, cmd, a, b, c)
        head += 1
        struct.pack_into("<I", shm, OFF_HEAD, head)

    def spawn(unit_type, count, owner, tx, ty):
        push(CMD_SPAWN, unit_type | (count << 16) | (owner << 24), tx * 32 + 16, ty * 32 + 16)

    def set_farmer(x, y):
        # Farmer (id 0) plus a cow (id 1) standing between him and the raiders.
        proxies = [(0, x, y, 0), (1, 44 * 32 + 16, 30 * 32 + 16, 2)]
        seq = struct.unpack_from("<I", shm, OFF_PROXIES)[0]
        struct.pack_into("<II", shm, OFF_PROXIES, seq + 1, len(proxies))
        for i, p in enumerate(proxies):
            struct.pack_into("<IiiI", shm, OFF_PROXIES + 8 + i * 16, *p)
        struct.pack_into("<I", shm, OFF_PROXIES, seq + 2)

    def proxy_damage():
        count = struct.unpack_from("<I", shm, OFF_PDMG)[0]
        return dict(struct.unpack_from("<II", shm, OFF_PDMG + 8 + i * 8) for i in range(count))

    def units():
        _, count, _, _ = struct.unpack_from("<IIII", shm, OFF_UNITS)
        return [struct.unpack_from("<IHBBiiiiii", shm, OFF_UNITS + 16 + i * 32) for i in range(count)]

    w, h = 80, 65
    grid = bytearray(1 if not (x == 40 and 10 <= y <= 50) else 0 for y in range(h) for x in range(w))
    shm[OFF_GRID:OFF_GRID + len(grid)] = bytes(grid)

    guest = subprocess.Popen([GUEST, DATA, BASE_MAP, SHM_NAME])
    try:
        deadline = time.time() + 30
        while struct.unpack_from("<I", shm, 28)[0] < 1:
            if guest.poll() is not None or time.time() > deadline:
                sys.exit("guest did not start")
            time.sleep(0.1)

        set_farmer(50 * 32 + 16, 30 * 32 + 16)
        push(CMD_LOAD_MAP, w, h, 1)
        spawn(30, 1, 0, 52, 28)    # siege tank
        spawn(124, 1, 0, 52, 32)   # missile turret
        spawn(37, 6, 1, 20, 30)    # zerglings
        spawn(38, 2, 1, 20, 34)    # hydras
        spawn(43, 2, 1, 70, 10)    # mutas
        spawn(65, 2, 2, 20, 26)    # zealots
        spawn(0, 4, 3, 70, 50)     # marines
        time.sleep(1)

        # Send one zergling to a "crop" far away from everything else.
        ling = next(u for u in units() if u[1] == 37)
        push(CMD_MOVE_TO, ling[0], 10 * 32 + 16, 60 * 32 + 16)

        hit = False
        for second in range(1, 21):
            set_farmer(50 * 32 + 16, 30 * 32 + 16)
            us = units()
            if not hit:
                zealot = next((u for u in us if u[1] == 65), None)
                if zealot:
                    push(CMD_DAMAGE, zealot[0], 120, 0)  # 60 shields + 60 hp of 100
                    hit = zealot[0]
            time.sleep(1)
            us = units()
            by_owner = {o: Counter(NAMES.get(u[1], u[1]) for u in us if u[2] == o) for o in range(4)}
            damage = struct.unpack_from("<I", shm, 32)[0]
            failures = struct.unpack_from("<I", shm, 40)[0]
            hit_zealot = next((f"zealot#{u[0]} hp={u[6]} sh={u[8]}" for u in us if u[0] == hit), "zealot dead")
            sent = next(((u[4] // 32, u[5] // 32) for u in us if u[0] == ling[0]), "dead")
            print(f"t={second:2d}s proxy_dmg={proxy_damage()} sent_ling_tile={sent} fails={failures} | farm {dict(by_owner[0])} | "
                  f"raiders {dict(by_owner[1] + by_owner[2] + by_owner[3])} | {hit_zealot}")
    finally:
        guest.terminate()


if __name__ == "__main__":
    main()
