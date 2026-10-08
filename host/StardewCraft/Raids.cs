using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;

namespace StardewCraft
{
    /// <summary>Sends numbered raid stages, either next to the farmer or in from the map edge.</summary>
    internal sealed class Raids
    {
        private readonly Shm shm;
        private readonly ModConfig config;

        public Raids(Shm shm, ModConfig config)
        {
            this.shm = shm;
            this.config = config;
        }

        /// <summary>The stage a night raid uses: one step harder every few in-game days.</summary>
        public int NightStage()
        {
            int stage = Game1.Date.TotalDays / Math.Max(1, this.config.DaysPerNightStage);
            return Math.Clamp(stage, 0, BwUnits.Stages.Count - 1);
        }

        /// <summary>Spawns one stage (0-based); returns an error for the HUD, or null on success.</summary>
        public string? Launch(SimLocation sim, int liveRaiders, int stage, bool fromEdge)
        {
            var groups = BwUnits.Stages[stage].Groups;
            if (liveRaiders + groups.Sum(g => g.Count) > this.config.MaxRaiders)
                return "Too many raiders already. Clear some first (F6).";

            Point farmer = Game1.player.TilePoint;
            int spawned = 0;
            foreach (var group in groups)
            {
                Point? at = fromEdge ? FindEdgeTile(sim) : FindTileNear(sim, farmer, this.config.SpawnDistanceTiles);
                if (at == null)
                    continue;
                this.shm.Spawn(group.Type, group.Count, group.Owner, at.Value.X * 32 + 16, at.Value.Y * 32 + 16);
                spawned++;
            }
            return spawned > 0 ? null : "No room for raiders here.";
        }

        private static Point? FindEdgeTile(SimLocation sim)
        {
            const int band = 4;
            for (int i = 0; i < 200; i++)
            {
                int side = Game1.random.Next(4);
                int along = Game1.random.Next(side < 2 ? sim.Width : sim.Height);
                int depth = Game1.random.Next(1, band);
                Point p = side switch
                {
                    0 => new Point(along, depth),
                    1 => new Point(along, sim.Height - 1 - depth),
                    2 => new Point(depth, along),
                    _ => new Point(sim.Width - 1 - depth, along),
                };
                if (sim.IsWalkable(p.X, p.Y))
                    return p;
            }
            return null;
        }

        public static Point? FindTileNear(SimLocation sim, Point center, int distance)
        {
            for (int ring = distance; ring > 1; ring--)
            {
                for (int i = 0; i < 16; i++)
                {
                    double angle = Game1.random.NextDouble() * Math.PI * 2;
                    int x = center.X + (int)Math.Round(Math.Cos(angle) * ring);
                    int y = center.Y + (int)Math.Round(Math.Sin(angle) * ring);
                    if (sim.IsWalkable(x, y))
                        return new Point(x, y);
                }
            }
            return null;
        }
    }
}
