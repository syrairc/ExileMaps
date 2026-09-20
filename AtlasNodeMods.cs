using System;
using System.Collections.Generic;
using ExileCore2.PoEMemory.FilesInMemory;
using ExileCore2.Shared.Enums;

namespace ExileMaps;

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

public static class AtlasModHelpers
{
    public static void AddModStats(ModsDat.ModRecord mod, List<AtlasStatValue> into, bool rolled, TinyMt32 rng)
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
}
