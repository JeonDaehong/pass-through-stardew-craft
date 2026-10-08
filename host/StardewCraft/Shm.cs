using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace StardewCraft
{
    /// <summary>Shared memory block shared with the OpenBW guest. Layout: docs/DESIGN.md, guest/shm.h.</summary>
    internal sealed class Shm : IDisposable
    {
        public const string Name = @"Local\StardewCraft_v1";
        public const int Size = 0x20000;
        public const uint Magic = 0x52434453;
        public const uint Version = 4;

        public const int OffHostPid = 8;
        public const int OffGuestPid = 12;
        public const int OffHostHeartbeat = 16;
        public const int OffGuestHeartbeat = 20;
        public const int OffGuestFrame = 24;
        public const int OffGuestStatus = 28;
        public const int OffFarmerDamage = 32;
        public const int OffSpawnFailures = 40;
        public const int OffProxies = 0x500;
        public const int OffProxyDamage = 0xA00;
        public const int ProxyCapacity = 64;
        public const int OffCmdHead = 0x80;
        public const int OffCmdTail = 0x84;
        public const int OffCommands = 0x88;
        public const int CommandCapacity = 64;
        public const int OffGrid = 0x1000;
        public const int GridMax = 256;
        public const int OffSnapshot = 0x12000;
        public const int ImageCapacity = 1024;
        public const int ImageEntrySize = 24;
        public const int OffUnits = 0x19000;
        public const int UnitCapacity = 512;
        public const int UnitEntrySize = 32;

        public const uint CmdLoadMap = 1;
        public const uint CmdSpawn = 2;
        public const uint CmdClear = 3;
        public const uint CmdDamage = 4;
        public const uint CmdMoveTo = 5;
        public const uint CmdAttackMoveTo = 6;
        public const uint CmdSpawnUnit = 7;

        private readonly MemoryMappedFile file;
        private readonly MemoryMappedViewAccessor view;
        private readonly byte[] snapshotBuffer = new byte[16 + ImageCapacity * ImageEntrySize];
        private readonly byte[] unitBuffer = new byte[16 + UnitCapacity * UnitEntrySize];
        private uint cmdHead;

        public Shm()
        {
            this.file = MemoryMappedFile.CreateOrOpen(Name, Size);
            this.view = this.file.CreateViewAccessor(0, Size);

            // Reset everything from a previous session before the guest attaches.
            this.view.WriteArray(0, new byte[Size], 0, Size);
            this.view.Write(0, Magic);
            this.view.Write(4, Version);
            this.view.Write(OffHostPid, (uint)Environment.ProcessId);
        }

        public uint GuestStatus => this.view.ReadUInt32(OffGuestStatus);
        public uint GuestFrame => this.view.ReadUInt32(OffGuestFrame);
        public uint FarmerDamageTotal => this.view.ReadUInt32(OffFarmerDamage);
        public uint SpawnFailures => this.view.ReadUInt32(OffSpawnFailures);

        public void Heartbeat()
        {
            this.view.Write(OffHostHeartbeat, this.view.ReadUInt32(OffHostHeartbeat) + 1);
        }

        /// <summary>Publishes everything raiders can attack (farmer, animals, villagers) under a seqlock.</summary>
        public void WriteProxies(IReadOnlyList<Proxy> proxies)
        {
            int count = Math.Min(proxies.Count, ProxyCapacity);
            uint seq = this.view.ReadUInt32(OffProxies);
            this.view.Write(OffProxies, seq + 1);
            Thread.MemoryBarrier();
            this.view.Write(OffProxies + 4, (uint)count);
            for (int i = 0; i < count; i++)
            {
                int o = OffProxies + 8 + i * 16;
                this.view.Write(o, proxies[i].Id);
                this.view.Write(o + 4, proxies[i].X);
                this.view.Write(o + 8, proxies[i].Y);
                this.view.Write(o + 12, (uint)proxies[i].Kind);
            }
            Thread.MemoryBarrier();
            this.view.Write(OffProxies, seq + 2);
        }

        /// <summary>Total damage each proxy has absorbed so far, by proxy id.</summary>
        public Dictionary<uint, uint> ReadProxyDamage()
        {
            var result = new Dictionary<uint, uint>();
            uint count = Math.Min(this.view.ReadUInt32(OffProxyDamage), ProxyCapacity);
            for (int i = 0; i < count; i++)
            {
                int o = OffProxyDamage + 8 + i * 8;
                result[this.view.ReadUInt32(o)] = this.view.ReadUInt32(o + 4);
            }
            return result;
        }

        public bool MoveTo(uint unitId, int x, int y) => this.PushCommand(CmdMoveTo, (int)unitId, x, y);

        public bool AttackMoveTo(uint unitId, int x, int y) => this.PushCommand(CmdAttackMoveTo, (int)unitId, x, y);

        public void WriteGrid(byte[] grid)
        {
            this.view.WriteArray(OffGrid, grid, 0, grid.Length);
        }

        public bool PushCommand(uint type, int a, int b, int c)
        {
            uint tail = this.view.ReadUInt32(OffCmdTail);
            if (this.cmdHead - tail >= CommandCapacity)
                return false;

            int offset = OffCommands + (int)(this.cmdHead % CommandCapacity) * 16;
            this.view.Write(offset, type);
            this.view.Write(offset + 4, a);
            this.view.Write(offset + 8, b);
            this.view.Write(offset + 12, c);
            Thread.MemoryBarrier();
            this.cmdHead++;
            this.view.Write(OffCmdHead, this.cmdHead);
            return true;
        }

        public bool Spawn(int unitType, int count, int owner, int x, int y)
        {
            return this.PushCommand(CmdSpawn, unitType | (count << 16) | (owner << 24), x, y);
        }

        /// <summary>Spawns one unit at an exact position with hpPercent (1-100) of its hit points.</summary>
        public bool SpawnUnit(int unitType, int owner, int hpPercent, int x, int y)
        {
            return this.PushCommand(CmdSpawnUnit, unitType | (owner << 16) | (Math.Clamp(hpPercent, 1, 100) << 24), x, y);
        }

        public bool Damage(uint unitId, int amount)
        {
            return this.PushCommand(CmdDamage, (int)unitId, amount, 0);
        }

        /// <summary>Copies a consistent unit table for the given map, or returns null.</summary>
        public UnitInfo[]? ReadUnits(int mapId)
        {
            for (int tries = 0; tries < 4; tries++)
            {
                uint seq0 = this.view.ReadUInt32(OffUnits);
                if ((seq0 & 1) != 0)
                    continue;
                Thread.MemoryBarrier();
                uint count = Math.Min(this.view.ReadUInt32(OffUnits + 4), UnitCapacity);
                this.view.ReadArray(OffUnits, this.unitBuffer, 0, 16 + (int)count * UnitEntrySize);
                Thread.MemoryBarrier();
                if (this.view.ReadUInt32(OffUnits) != seq0)
                    continue;
                if (BitConverter.ToUInt32(this.unitBuffer, 8) != (uint)mapId)
                    return null;

                var units = new UnitInfo[count];
                for (int i = 0; i < count; i++)
                    units[i] = new UnitInfo(this.unitBuffer, 16 + i * UnitEntrySize);
                return units;
            }
            return null;
        }

        /// <summary>Copies a consistent image snapshot, or returns null if the guest kept writing.</summary>
        public Snapshot? ReadSnapshot()
        {
            for (int tries = 0; tries < 4; tries++)
            {
                uint seq0 = this.view.ReadUInt32(OffSnapshot);
                if ((seq0 & 1) != 0)
                    continue;
                Thread.MemoryBarrier();
                uint count = Math.Min(this.view.ReadUInt32(OffSnapshot + 4), ImageCapacity);
                this.view.ReadArray(OffSnapshot, this.snapshotBuffer, 0, 16 + (int)count * ImageEntrySize);
                Thread.MemoryBarrier();
                if (this.view.ReadUInt32(OffSnapshot) == seq0)
                    return new Snapshot(this.snapshotBuffer, (int)count);
            }
            return null;
        }

        public void Dispose()
        {
            this.view.Dispose();
            this.file.Dispose();
        }
    }

    internal readonly struct ImageEntry
    {
        public readonly ushort ImageId;
        public readonly ushort Frame;
        public readonly int X;
        public readonly int Y;
        public readonly bool Flipped;
        public readonly byte Modifier;
        public readonly byte Color;
        public readonly uint UnitId;

        public ImageEntry(byte[] b, int o)
        {
            this.ImageId = BitConverter.ToUInt16(b, o);
            this.Frame = BitConverter.ToUInt16(b, o + 2);
            this.X = BitConverter.ToInt32(b, o + 4);
            this.Y = BitConverter.ToInt32(b, o + 8);
            this.Flipped = (b[o + 12] & 1) != 0;
            this.Modifier = b[o + 13];
            this.Color = b[o + 14];
            this.UnitId = BitConverter.ToUInt32(b, o + 16);
        }
    }

    internal enum ProxyKind : uint
    {
        Farmer = 0,
        SmallAnimal = 1,
        LargeAnimal = 2,
        Villager = 3,
    }

    internal readonly record struct Proxy(uint Id, int X, int Y, ProxyKind Kind);

    internal readonly struct UnitInfo
    {
        public readonly uint Id;
        public readonly int Type;
        public readonly int Owner;
        public readonly bool Flying;
        public readonly bool Building;
        public readonly int X;
        public readonly int Y;
        public readonly int Hp;
        public readonly int MaxHp;
        public readonly int Shields;
        public readonly int MaxShields;

        public UnitInfo(byte[] b, int o)
        {
            this.Id = BitConverter.ToUInt32(b, o);
            this.Type = BitConverter.ToUInt16(b, o + 4);
            this.Owner = b[o + 6];
            this.Flying = (b[o + 7] & 1) != 0;
            this.Building = (b[o + 7] & 2) != 0;
            this.X = BitConverter.ToInt32(b, o + 8);
            this.Y = BitConverter.ToInt32(b, o + 12);
            this.Hp = BitConverter.ToInt32(b, o + 16);
            this.MaxHp = BitConverter.ToInt32(b, o + 20);
            this.Shields = BitConverter.ToInt32(b, o + 24);
            this.MaxShields = BitConverter.ToInt32(b, o + 28);
        }

        public bool IsRaider => this.Owner >= BwUnits.FirstRaider && this.Owner <= BwUnits.LastRaider;

        /// <summary>Center in Stardew world pixels.</summary>
        public Microsoft.Xna.Framework.Vector2 WorldPosition => new(this.X * 2, this.Y * 2);
    }

    internal readonly struct Snapshot
    {
        private readonly byte[] buffer;
        public readonly int Count;
        public uint MapId => BitConverter.ToUInt32(this.buffer, 8);

        public Snapshot(byte[] buffer, int count)
        {
            this.buffer = buffer;
            this.Count = count;
        }

        public ImageEntry this[int i] => new(this.buffer, 16 + i * Shm.ImageEntrySize);
    }
}
