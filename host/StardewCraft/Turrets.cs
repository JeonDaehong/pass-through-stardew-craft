using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using SObject = StardewValley.Object;

namespace StardewCraft
{
    /// <summary>Farm defences paid for with crops, remembered per location in the save file.</summary>
    internal sealed class Turrets
    {
        private const string SaveKey = "turrets";
        private const int RefundCheckTicks = 30;

        public sealed class TurretRecord
        {
            public int Type { get; set; }
            public int X { get; set; } // BW px, unit center
            public int Y { get; set; }
        }

        private sealed class PendingBuild
        {
            public SObject? Payment; // null when the build was free
            public uint FailuresBefore;
            public int Ticks;
            public string Name = "";
        }

        private readonly IModHelper helper;
        private readonly Shm shm;
        private Dictionary<string, List<TurretRecord>> byLocation = new();
        private readonly List<PendingBuild> pending = new();

        public Turrets(IModHelper helper, Shm shm)
        {
            this.helper = helper;
            this.shm = shm;
        }

        public void Load()
        {
            this.byLocation = this.helper.Data.ReadSaveData<Dictionary<string, List<TurretRecord>>>(SaveKey) ?? new();
        }

        public void Save()
        {
            if (Context.IsMainPlayer)
                this.helper.Data.WriteSaveData(SaveKey, this.byLocation);
        }

        /// <summary>Recreates the location's surviving defences right after its map is loaded.</summary>
        public void Restore(SimLocation sim)
        {
            if (!this.byLocation.TryGetValue(sim.Key, out var list))
                return;
            foreach (var t in list)
                this.shm.Spawn(t.Type, 1, BwUnits.FarmerOwner, t.X, t.Y);
        }

        /// <summary>Tracks which defences are still standing, so destroyed ones stay destroyed.</summary>
        public void Observe(SimLocation sim, IReadOnlyList<UnitInfo> units)
        {
            this.byLocation[sim.Key] = units
                .Where(u => u.Owner == BwUnits.FarmerOwner)
                .Select(u => new TurretRecord { Type = u.Type, X = u.X, Y = u.Y })
                .ToList();
        }

        /// <summary>Builds a defence at a tile, paying with the held crop stack unless it is free.</summary>
        public string? TryBuild(SimLocation sim, BwUnits.TurretKind kind, int tileX, int tileY, bool free)
        {
            if (free)
            {
                if (!sim.IsWalkable(tileX, tileY, kind.TileSize))
                    return $"Can't build a {kind.Name} there.";
                this.Place(kind, tileX, tileY, payment: null);
                return null;
            }

            if (Game1.player.ActiveObject is not SObject crop || !IsCrop(crop))
                return $"Hold crops to pay for a {kind.Name} ({kind.CropValue}g of crops).";

            int price = Math.Max(1, crop.Price);
            int needed = (kind.CropValue + price - 1) / price;
            if (crop.Stack < needed)
                return $"A {kind.Name} costs {needed} {crop.DisplayName} ({kind.CropValue}g). You have {crop.Stack}.";

            if (!sim.IsWalkable(tileX, tileY, kind.TileSize))
                return $"Can't build a {kind.Name} there.";

            var payment = (SObject)crop.getOne();
            payment.Stack = needed;
            crop.Stack -= needed;
            if (crop.Stack <= 0)
                Game1.player.removeItemFromInventory(crop);

            this.Place(kind, tileX, tileY, payment);
            return null;
        }

        private void Place(BwUnits.TurretKind kind, int tileX, int tileY, SObject? payment)
        {
            int half = kind.TileSize * 16;
            this.shm.Spawn(kind.Type, 1, BwUnits.FarmerOwner, tileX * 32 + half, tileY * 32 + half);
            this.pending.Add(new PendingBuild { Payment = payment, FailuresBefore = this.shm.SpawnFailures, Name = kind.Name });
            Game1.playSound("axchop");
        }

        /// <summary>Reports (and refunds) builds the guest could not place, e.g. because a unit was standing there.</summary>
        public void Update()
        {
            for (int i = this.pending.Count - 1; i >= 0; i--)
            {
                var build = this.pending[i];
                if (++build.Ticks < RefundCheckTicks)
                    continue;
                if (this.shm.SpawnFailures > build.FailuresBefore)
                {
                    string refund = "";
                    if (build.Payment != null)
                    {
                        Game1.player.addItemByMenuIfNecessary(build.Payment);
                        refund = "; crops refunded";
                    }
                    Game1.addHUDMessage(new HUDMessage($"The {build.Name} didn't fit there{refund}.", HUDMessage.error_type));
                }
                this.pending.RemoveAt(i);
            }
        }

        private static bool IsCrop(SObject obj)
        {
            return obj.Category is SObject.VegetableCategory or SObject.FruitsCategory or SObject.flowersCategory;
        }
    }
}
