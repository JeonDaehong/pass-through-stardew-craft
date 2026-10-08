using System.Collections.Generic;

namespace StardewCraft
{
    /// <summary>StarCraft unit ids (units.dat order) and the raid roster.</summary>
    internal static class BwUnits
    {
        public const int FarmerOwner = 0;
        public const int ZergOwner = 1;
        public const int ProtossOwner = 2;
        public const int TerranOwner = 3;
        public const int FirstRaider = ZergOwner;
        public const int LastRaider = TerranOwner;

        public const int Marine = 0;
        public const int Vulture = 2;
        public const int Goliath = 3;
        public const int SiegeTankSieged = 30;
        public const int Firebat = 32;
        public const int Zergling = 37;
        public const int Hydralisk = 38;
        public const int Ultralisk = 39;
        public const int Mutalisk = 43;
        public const int Guardian = 44;
        public const int Zealot = 65;
        public const int Dragoon = 66;
        public const int Archon = 68;
        public const int MissileTurret = 124;

        public sealed record RaidGroup(int Type, int Count, int Owner);

        /// <summary>A fixed raid the farmer can summon by number.</summary>
        public sealed record Stage(string Name, RaidGroup[] Groups);

        public static readonly IReadOnlyList<Stage> Stages = new Stage[]
        {
            new("Zergling scouts", new RaidGroup[] { new(Zergling, 4, ZergOwner) }),
            new("Ling & marine", new RaidGroup[] { new(Zergling, 6, ZergOwner), new(Marine, 4, TerranOwner) }),
            new("Zealots & hydras", new RaidGroup[] { new(Zealot, 2, ProtossOwner), new(Hydralisk, 3, ZergOwner) }),
            new("Firebats & dragoons", new RaidGroup[] { new(Firebat, 3, TerranOwner), new(Dragoon, 2, ProtossOwner) }),
            new("Air raid", new RaidGroup[] { new(Mutalisk, 3, ZergOwner), new(Vulture, 3, TerranOwner) }),
            new("Mech push", new RaidGroup[] { new(Goliath, 2, TerranOwner), new(Hydralisk, 4, ZergOwner), new(Zergling, 6, ZergOwner) }),
            new("Ultralisk charge", new RaidGroup[] { new(Ultralisk, 1, ZergOwner), new(Zergling, 8, ZergOwner) }),
            new("Guardian siege", new RaidGroup[] { new(Guardian, 2, ZergOwner), new(Mutalisk, 4, ZergOwner) }),
            new("Protoss army", new RaidGroup[] { new(Archon, 1, ProtossOwner), new(Zealot, 4, ProtossOwner), new(Dragoon, 2, ProtossOwner) }),
            new("BOSS: everything", new RaidGroup[]
            {
                new(Ultralisk, 2, ZergOwner), new(Archon, 2, ProtossOwner), new(Guardian, 2, ZergOwner),
                new(Zergling, 12, ZergOwner), new(Goliath, 2, TerranOwner),
            }),
        };

        /// <summary>A defence the farmer can buy with crops.</summary>
        public sealed record TurretKind(string Name, int Type, int TileSize, int CropValue);

        public static readonly TurretKind SiegeTank = new("Siege Tank", SiegeTankSieged, 1, 500);
        public static readonly TurretKind AntiAir = new("Missile Turret", MissileTurret, 2, 250);
    }
}
