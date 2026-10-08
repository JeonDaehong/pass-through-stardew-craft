using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace StardewCraft
{
    /// <summary>Sends some ground raiders off to eat crops instead of chasing the farmer.</summary>
    internal sealed class CropRaiders
    {
        private const int UpdateEvery = 15;   // ticks
        private const int EatTicks = 90;      // standing on a crop this long destroys it
        private const int ReorderTicks = 360; // re-send the move order before the guest's hold expires
        private const int MaxEatersPerCrop = 2;

        private static readonly HashSet<int> Grazers = new()
        {
            BwUnits.Zergling, BwUnits.Hydralisk, BwUnits.Ultralisk, BwUnits.Zealot, BwUnits.Marine, BwUnits.Firebat,
        };

        private sealed class Mission
        {
            public Point Tile;
            public int Eaten;
            public int SinceOrder;
        }

        private readonly Shm shm;
        private readonly ModConfig config;
        private readonly Dictionary<uint, Mission> missions = new();
        private readonly HashSet<uint> decided = new();
        private int tick;
        private int lastWarningTick = -100000;

        public CropRaiders(Shm shm, ModConfig config)
        {
            this.shm = shm;
            this.config = config;
        }

        public void Reset()
        {
            this.missions.Clear();
            this.decided.Clear();
        }

        public void Update(GameLocation location, IReadOnlyList<UnitInfo> units)
        {
            if (++this.tick % UpdateEvery != 0)
                return;

            var alive = units.Where(u => u.IsRaider).ToDictionary(u => u.Id);
            foreach (uint gone in this.missions.Keys.Where(id => !alive.ContainsKey(id)).ToList())
                this.missions.Remove(gone);
            this.decided.IntersectWith(alive.Keys);

            List<Point> crops = Crops(location);
            if (crops.Count == 0)
            {
                this.missions.Clear();
                return;
            }

            foreach (var unit in alive.Values)
            {
                if (unit.Flying || !Grazers.Contains(unit.Type) || this.decided.Contains(unit.Id))
                    continue;
                this.decided.Add(unit.Id);
                if (Game1.random.NextDouble() < this.config.CropRaiderChance)
                    this.Assign(unit, crops);
            }

            foreach (var (id, mission) in this.missions.ToList())
            {
                var unit = alive[id];
                if (!HasCrop(location, mission.Tile))
                {
                    this.Assign(unit, crops);
                    continue;
                }

                mission.SinceOrder += UpdateEvery;
                var center = new Vector2(mission.Tile.X * 32 + 16, mission.Tile.Y * 32 + 16);
                if (Vector2.Distance(center, new Vector2(unit.X, unit.Y)) <= 24)
                {
                    mission.Eaten += UpdateEvery;
                    if (mission.Eaten >= EatTicks)
                    {
                        this.EatCrop(location, mission.Tile);
                        crops.Remove(mission.Tile);
                        this.Assign(unit, crops);
                    }
                }
                else if (mission.SinceOrder >= ReorderTicks)
                {
                    mission.SinceOrder = 0;
                    this.shm.MoveTo(id, (int)center.X, (int)center.Y);
                }
            }
        }

        private void Assign(UnitInfo unit, List<Point> crops)
        {
            var taken = this.missions.Where(m => m.Key != unit.Id).GroupBy(m => m.Value.Tile).ToDictionary(g => g.Key, g => g.Count());
            Point? best = null;
            float bestDist = float.MaxValue;
            foreach (Point tile in crops)
            {
                if (taken.TryGetValue(tile, out int n) && n >= MaxEatersPerCrop)
                    continue;
                float d = Vector2.DistanceSquared(new Vector2(tile.X * 32 + 16, tile.Y * 32 + 16), new Vector2(unit.X, unit.Y));
                if (d < bestDist)
                {
                    best = tile;
                    bestDist = d;
                }
            }

            if (best == null)
            {
                // Nothing left to eat; let the guest send it back after the farmer.
                this.missions.Remove(unit.Id);
                return;
            }
            this.missions[unit.Id] = new Mission { Tile = best.Value };
            this.shm.MoveTo(unit.Id, best.Value.X * 32 + 16, best.Value.Y * 32 + 16);
        }

        private void EatCrop(GameLocation location, Point tile)
        {
            if (location.terrainFeatures.TryGetValue(new Vector2(tile.X, tile.Y), out var feature) && feature is HoeDirt dirt && dirt.crop != null)
            {
                dirt.destroyCrop(true);
                location.playSound("cut");
                if (this.tick - this.lastWarningTick > 60 * 20)
                {
                    this.lastWarningTick = this.tick;
                    Game1.addHUDMessage(new HUDMessage("Raiders are eating your crops!", HUDMessage.error_type));
                }
            }
        }

        private static bool HasCrop(GameLocation location, Point tile)
        {
            return location.terrainFeatures.TryGetValue(new Vector2(tile.X, tile.Y), out var f)
                && f is HoeDirt { crop: not null } dirt && !dirt.crop.dead.Value;
        }

        private static List<Point> Crops(GameLocation location)
        {
            var result = new List<Point>();
            foreach (var pair in location.terrainFeatures.Pairs)
            {
                if (pair.Value is HoeDirt { crop: not null } dirt && !dirt.crop.dead.Value)
                    result.Add(new Point((int)pair.Key.X, (int)pair.Key.Y));
            }
            return result;
        }
    }
}
