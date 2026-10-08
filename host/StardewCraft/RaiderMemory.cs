using System;
using System.Collections.Generic;
using System.Linq;

namespace StardewCraft
{
    /// <summary>
    /// Remembers the raiders left behind in each location, so leaving and coming back
    /// doesn't make them vanish. Forgotten every morning.
    /// </summary>
    internal sealed class RaiderMemory
    {
        private sealed record Raider(int Type, int Owner, int X, int Y, int HpPercent);

        private readonly Shm shm;
        private readonly Dictionary<string, List<Raider>> byLocation = new();

        public RaiderMemory(Shm shm)
        {
            this.shm = shm;
        }

        public void Clear()
        {
            this.byLocation.Clear();
        }

        /// <summary>Keeps the latest raider list for the simulated location.</summary>
        public void Observe(SimLocation sim, IReadOnlyList<UnitInfo> units)
        {
            this.byLocation[sim.Key] = units
                .Where(u => u.IsRaider && !u.Building)
                .Select(u => new Raider(u.Type, u.Owner, u.X, u.Y, u.MaxHp > 0 ? Math.Max(1, u.Hp * 100 / u.MaxHp) : 100))
                .ToList();
        }

        /// <summary>Puts the location's remembered raiders back right after its map loads.</summary>
        public int Restore(SimLocation sim)
        {
            if (!this.byLocation.TryGetValue(sim.Key, out var raiders))
                return 0;
            foreach (var r in raiders)
                this.shm.SpawnUnit(r.Type, r.Owner, r.HpPercent, r.X, r.Y);
            return raiders.Count;
        }
    }
}
