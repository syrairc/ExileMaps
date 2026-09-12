using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ExileCore2.PoEMemory;
using ExileCore2.PoEMemory.FilesInMemory;
using ExileCore2.Shared.Enums;
using ExileCore2.Shared.Interfaces;
using GameOffsets2.Native;

namespace ExileMaps;

[StructLayout(LayoutKind.Explicit, Pack = 1)]
public struct AtlasPinOffsets
{
    [FieldOffset(0x2F0)] public long MapRow;
    [FieldOffset(0x310)] public int CoordX;
    [FieldOffset(0x314)] public int CoordY;
    [FieldOffset(0x318)] public int RollSeed;
    [FieldOffset(0x31C)] public byte RollTier;
    [FieldOffset(0x31D)] public byte RollSalt;
    [FieldOffset(0x31F)] public byte Flags;
    [FieldOffset(0x321)] public byte FissureFamily;
    [FieldOffset(0x322)] public byte FissureDepth;
    [FieldOffset(0x325)] public byte RollIsCleansed;
    [FieldOffset(0x350)] public StdVector Stats;
    [FieldOffset(0x3A8)] public long TooltipRowContainer;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct AtlasNodeStat
{
    public int Id;
    public int Value;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct RolledModRow
{
    public long ModRow;
    public long Owner;
    public long WeightCount;
    public long Weights;
    public byte Disabled;
}

[StructLayout(LayoutKind.Explicit, Pack = 1)]
public struct WorldAreaOffsets
{
    [FieldOffset(0x10D)] public long ModCount;
    [FieldOffset(0x115)] public long Mods;
    [FieldOffset(0x18B)] public byte EmitsMapMods;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct RitualLineRecord
{
    public int CoordX;
    public int CoordY;
    public StdVector Mods;
}

[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 0x40)]
public struct RitualLineModEntry
{
    [FieldOffset(0x00)] public StdVector Values;
    [FieldOffset(0x28)] public long ModRow;
}

public readonly record struct AtlasStatValue(string Key, GameStat Stat, StatType Type, int Value);

public sealed class TinyMt32
{
    private const uint Mat1 = 0x8F7011EEu;
    private const uint Mat2 = 0xFC78FF1Fu;
    private const uint Tmat = 0x3793FDFFu;

    private readonly uint[] state = new uint[4];

    private static uint Mix1(uint x) => (x ^ (x >> 27)) * 1664525u;

    private static uint Mix2(uint x) => (x ^ (x >> 27)) * 1566083941u;

    public void InitByArray(params uint[] key)
    {
        state[0] = 0;
        state[1] = Mat1;
        state[2] = Mat2;
        state[3] = Tmat;

        uint r = Mix1(state[0] ^ state[1] ^ state[3]);
        state[1] += r;
        r += (uint)key.Length;
        state[2] += r;
        state[0] = r;

        int count = Math.Max(key.Length + 1, 8) - 1;
        int i = 1;
        int j = 0;

        for (; j < key.Length && j < count; j++, i = (i + 1) % 4) {
            r = Mix1(state[i] ^ state[(i + 1) % 4] ^ state[(i + 3) % 4]);
            state[(i + 1) % 4] += r;
            r += key[j] + (uint)i;
            state[(i + 2) % 4] += r;
            state[i] = r;
        }

        for (; j < count; j++, i = (i + 1) % 4) {
            r = Mix1(state[i] ^ state[(i + 1) % 4] ^ state[(i + 3) % 4]);
            state[(i + 1) % 4] += r;
            r += (uint)i;
            state[(i + 2) % 4] += r;
            state[i] = r;
        }

        for (int round = 0; round < 4; round++, i = (i + 1) % 4) {
            r = Mix2(state[i] + state[(i + 1) % 4] + state[(i + 3) % 4]);
            state[(i + 1) % 4] ^= r;
            r -= (uint)i;
            state[(i + 2) % 4] ^= r;
            state[i] = r;
        }

        for (int step = 0; step < 8; step++)
            Next();
    }

    public uint Next()
    {
        uint x = (state[0] & 0x7FFFFFFFu) ^ state[1] ^ state[2];
        uint y = state[3] ^ (state[3] << 1);
        uint next3 = y ^ x ^ (x >> 1);
        uint mask = (uint)(-(int)(next3 & 1));

        uint next0 = state[1];
        uint next2 = y ^ (next3 << 10) ^ (mask & Mat2);

        state[1] = state[2] ^ (mask & Mat1);
        state[0] = next0;
        state[2] = next2;
        state[3] = next3;

        uint tempered = next0 + (next2 >> 8);
        return next3 ^ tempered ^ ((uint)(-(int)(tempered & 1)) & Tmat);
    }

    public uint NextRange(uint range)
    {
        if (range <= 1)
            return 0;

        uint value;
        do {
            value = Next();
        }
        while (value / range >= uint.MaxValue / range && uint.MaxValue % range != range - 1);

        return value % range;
    }

    public int NextBoundedInclusive(int min, int max)
    {
        uint low = (uint)min ^ 0x80000000u;
        uint high = (uint)max ^ 0x80000000u;
        uint span = high - low;
        uint value = span == 0xFFFFFFFFu ? Next() : NextRange(span + 1);
        return (int)((value + low) ^ 0x80000000u);
    }
}

public sealed class AtlasNodeModReader(IMemory memory, FilesContainer files)
{
    private const int MaxNodeStats = 128;
    private const int MaxRolledModRows = 512;
    private const int MaxRolledModWeights = 64;
    private const int MaxRitualRecords = 64;
    private const int MaxRitualMods = 16;
    private const int MaxRitualValues = 16;
    private const int MaxMapMods = 32;
    private const int MapModRefSize = 16;
    private const int FissureMaxDepth = 6;
    private const int RolledModRowsHolder = 0x28;

    private const string CleansedModsFile = "Data/Balance/EndgameCleansedMods.dat";
    private const string CorruptionModsFile = "Data/Balance/EndgameCorruptionMods.dat";

    private const int RitualPageOwner = 0x308;
    private const int RitualSession = 0x1B0;
    private const int RitualLine = 0x3A48;

    private static readonly string[] FissureFamilies = ["BlackBlood", "Lightless", "OfThePit"];

    private readonly Dictionary<string, RolledModRow[]> rolledModTables = [];
    private readonly TinyMt32 rng = new();

    public AtlasPinOffsets ReadPin(long address) => memory.Read<AtlasPinOffsets>(address);

    public static int Fingerprint(in AtlasPinOffsets pin, long ritualLineStamp)
    {
        var roll = HashCode.Combine(pin.RollSeed, pin.RollTier, pin.RollSalt, pin.Flags,
            pin.FissureFamily, pin.FissureDepth, pin.RollIsCleansed, pin.MapRow);
        return HashCode.Combine(roll, pin.Stats.First, pin.Stats.Last, ritualLineStamp);
    }

    public long ReadRitualLineStamp(long line)
    {
        if (line == 0)
            return 0;

        var records = memory.Read<StdVector>(line);
        return records.First ^ (records.Last << 1);
    }

    public long ReadRitualLine(long atlasPageAddress)
    {
        if (atlasPageAddress == 0)
            return 0;

        long owner = memory.Read<long>(atlasPageAddress + RitualPageOwner);
        if (owner == 0)
            return 0;

        long session = memory.Read<long>(owner + RitualSession);
        return session == 0 ? 0 : memory.Read<long>(session + RitualLine);
    }

    public void Collect(in AtlasPinOffsets pin, long ritualLine, List<AtlasStatValue> into)
    {
        ReadNodeStats(pin, into);
        ReadFissureMods(pin, into);
        ReadRolledMod(pin, into);
        ReadUniqueMapMods(pin, into);
        ReadRitualMods(ritualLine, pin, into);
    }

    private void ReadNodeStats(in AtlasPinOffsets pin, List<AtlasStatValue> into)
    {
        if (!Sane(pin.Stats, Unsafe.SizeOf<AtlasNodeStat>(), MaxNodeStats))
            return;

        var records = files.Stats?.recordsById;
        if (records == null)
            return;

        foreach (var entry in memory.ReadStdVector<AtlasNodeStat>(pin.Stats))
            if (entry.Value != 0 && records.TryGetValue(entry.Id, out var stat) && !string.IsNullOrEmpty(stat.Key))
                into.Add(new AtlasStatValue(stat.Key, stat.MatchingStat, stat.Type, entry.Value));
    }

    private void ReadFissureMods(in AtlasPinOffsets pin, List<AtlasStatValue> into)
    {
        if (pin.FissureFamily < 1 || pin.FissureFamily > FissureFamilies.Length)
            return;

        int tiers = Math.Min(pin.FissureDepth + 1, FissureMaxDepth);
        for (int tier = 1; tier <= tiers; tier++) {
            string key = $"AtlasAbyssCrack{FissureFamilies[pin.FissureFamily - 1]}{tier}";
            if (!files.Mods.records.TryGetValue(key, out var mod))
                continue;
            AddModStats(mod, into, rolled: false);
        }
    }

    private void ReadRolledMod(in AtlasPinOffsets pin, List<AtlasStatValue> into)
    {
        if ((pin.Flags & 4) == 0)
            return;

        var rows = RolledModTable(pin.RollIsCleansed != 0 ? CleansedModsFile : CorruptionModsFile);
        if (rows.Length == 0)
            return;

        rng.InitByArray((uint)pin.RollSeed, (uint)pin.CoordX, (uint)pin.CoordY, pin.RollSalt);

        long modRow = PickRolledMod(rows, pin.RollTier);
        if (modRow == 0)
            return;

        AddModStats(files.Mods.GetModByAddress(modRow), into, rolled: true);
    }

    private long PickRolledMod(RolledModRow[] rows, byte tier)
    {
        long chosen = 0;
        int total = 0;

        foreach (var row in rows) {
            if (row.WeightCount <= 0 || row.WeightCount > MaxRolledModWeights || row.Weights == 0)
                continue;

            long index = Math.Min(tier, row.WeightCount - 1);
            int weight = memory.Read<int>(row.Weights + index * sizeof(int));
            if (weight <= 0)
                continue;

            total += weight;
            if (rng.NextRange((uint)total) < weight)
                chosen = row.ModRow;
        }

        return chosen;
    }

    private void ReadUniqueMapMods(in AtlasPinOffsets pin, List<AtlasStatValue> into)
    {
        if (pin.MapRow == 0)
            return;

        long worldArea = memory.Read<long>(pin.MapRow);
        if (worldArea == 0)
            return;

        var area = memory.Read<WorldAreaOffsets>(worldArea);
        if (area.EmitsMapMods == 0 || area.ModCount <= 0 || area.ModCount > MaxMapMods || area.Mods == 0)
            return;

        for (int i = 0; i < area.ModCount; i++) {
            long modRow = memory.Read<long>(area.Mods + i * MapModRefSize);
            if (modRow == 0)
                continue;

            rng.InitByArray((uint)i, (uint)pin.RollSeed, (uint)pin.CoordX, (uint)pin.CoordY, pin.RollSalt);
            AddModStats(files.Mods.GetModByAddress(modRow), into, rolled: true);
        }
    }

    private void AddModStats(ModsDat.ModRecord mod, List<AtlasStatValue> into, bool rolled)
    {
        if (mod?.StatNames == null || mod.StatRange == null)
            return;

        int n = Math.Min(mod.StatNames.Length, mod.StatRange.Length);
        for (int i = 0; i < n && mod.StatNames[i] != null; i++) {
            var range = mod.StatRange[i];
            int value = !rolled ? range.Max
                : range.Min == range.Max ? range.Min
                : rng.NextBoundedInclusive(range.Min, range.Max);

            var stat = mod.StatNames[i];
            if (!string.IsNullOrEmpty(stat.Key))
                into.Add(new AtlasStatValue(stat.Key, stat.MatchingStat, stat.Type, value));
        }
    }

    private RolledModRow[] RolledModTable(string fileName)
    {
        if (rolledModTables.TryGetValue(fileName, out var cached))
            return cached;

        long handle = files.FindFile(fileName);
        if (handle == 0)
            return [];

        long rowsHolder = memory.Read<long>(handle + RolledModRowsHolder);
        if (rowsHolder == 0)
            return [];

        var rows = memory.Read<StdVector>(rowsHolder);
        if (!Sane(rows, Unsafe.SizeOf<RolledModRow>(), MaxRolledModRows))
            return [];

        return rolledModTables[fileName] = memory.ReadStdVector<RolledModRow>(rows);
    }

    private void ReadRitualMods(long line, in AtlasPinOffsets pin, List<AtlasStatValue> into)
    {
        if (line == 0)
            return;

        var records = memory.Read<StdVector>(line);
        if (!Sane(records, Unsafe.SizeOf<RitualLineRecord>(), MaxRitualRecords))
            return;

        foreach (var record in memory.ReadStdVector<RitualLineRecord>(records)) {
            if (record.CoordX != pin.CoordX || record.CoordY != pin.CoordY)
                continue;
            if (!Sane(record.Mods, Unsafe.SizeOf<RitualLineModEntry>(), MaxRitualMods))
                return;

            foreach (var entry in memory.ReadStdVector<RitualLineModEntry>(record.Mods)) {
                if (entry.ModRow == 0)
                    continue;

                var mod = files.Mods.GetModByAddress(entry.ModRow);
                if (mod?.StatNames == null || !Sane(entry.Values, sizeof(int), MaxRitualValues))
                    continue;

                int[] values = memory.ReadStdVector<int>(entry.Values);
                for (int i = 0; i < values.Length && i < mod.StatNames.Length && mod.StatNames[i] != null; i++) {
                    var stat = mod.StatNames[i];
                    if (!string.IsNullOrEmpty(stat.Key))
                        into.Add(new AtlasStatValue(stat.Key, stat.MatchingStat, stat.Type, values[i]));
                }
            }

            return;
        }
    }

    private static bool Sane(StdVector vector, int elementSize, int maxElements) =>
        vector.First != 0 && vector.Last > vector.First && vector.TotalElements(elementSize) <= maxElements;
}
