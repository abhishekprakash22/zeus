// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// Bob Jenkins' lookup3 hashlittle() — the callsign hash WSPR uses for type-3
// messages and the decoder's hash table (wsprd's nhash.c). Byte-wise form:
// on little-endian hosts it yields exactly what nhash.c's aligned-word path
// does (lookup3 is defined that way), and it never reads past the key.
//
// Original: "By Bob Jenkins, 2006. You may use this code any way you wish,
// private, educational, or commercial. It's free."

using System.Numerics;

namespace Zeus.Server.Hosting.Digital.Wspr;

internal static class Lookup3
{
    /// <summary>wsprd's nhash(): hashlittle() masked to 15 bits (the size of
    /// its 32768-entry callsign table). The C skips the mask for a zero-length
    /// key and would then index far outside the table; here it always masks.</summary>
    public static int Nhash(ReadOnlySpan<byte> key, uint initval) =>
        (int)(HashLittle(key, initval) & 32767);

    public static uint HashLittle(ReadOnlySpan<byte> key, uint initval)
    {
        uint a, b, c;
        a = b = c = 0xdeadbeef + (uint)key.Length + initval;

        int length = key.Length, o = 0;
        while (length > 12)
        {
            a += key[o] + ((uint)key[o + 1] << 8) + ((uint)key[o + 2] << 16) + ((uint)key[o + 3] << 24);
            b += key[o + 4] + ((uint)key[o + 5] << 8) + ((uint)key[o + 6] << 16) + ((uint)key[o + 7] << 24);
            c += key[o + 8] + ((uint)key[o + 9] << 8) + ((uint)key[o + 10] << 16) + ((uint)key[o + 11] << 24);
            Mix(ref a, ref b, ref c);
            length -= 12;
            o += 12;
        }

        switch (length)                       // the last (possibly partial) block
        {
            case 12: c += (uint)key[o + 11] << 24; goto case 11;
            case 11: c += (uint)key[o + 10] << 16; goto case 10;
            case 10: c += (uint)key[o + 9] << 8; goto case 9;
            case 9: c += key[o + 8]; goto case 8;
            case 8: b += (uint)key[o + 7] << 24; goto case 7;
            case 7: b += (uint)key[o + 6] << 16; goto case 6;
            case 6: b += (uint)key[o + 5] << 8; goto case 5;
            case 5: b += key[o + 4]; goto case 4;
            case 4: a += (uint)key[o + 3] << 24; goto case 3;
            case 3: a += (uint)key[o + 2] << 16; goto case 2;
            case 2: a += (uint)key[o + 1] << 8; goto case 1;
            case 1: a += key[o]; break;
            case 0: return c;                 // zero length requires no mixing
        }

        Final(ref a, ref b, ref c);
        return c;
    }

    private static void Mix(ref uint a, ref uint b, ref uint c)
    {
        a -= c; a ^= BitOperations.RotateLeft(c, 4); c += b;
        b -= a; b ^= BitOperations.RotateLeft(a, 6); a += c;
        c -= b; c ^= BitOperations.RotateLeft(b, 8); b += a;
        a -= c; a ^= BitOperations.RotateLeft(c, 16); c += b;
        b -= a; b ^= BitOperations.RotateLeft(a, 19); a += c;
        c -= b; c ^= BitOperations.RotateLeft(b, 4); b += a;
    }

    private static void Final(ref uint a, ref uint b, ref uint c)
    {
        c ^= b; c -= BitOperations.RotateLeft(b, 14);
        a ^= c; a -= BitOperations.RotateLeft(c, 11);
        b ^= a; b -= BitOperations.RotateLeft(a, 25);
        c ^= b; c -= BitOperations.RotateLeft(b, 16);
        a ^= c; a -= BitOperations.RotateLeft(c, 4);
        b ^= a; b -= BitOperations.RotateLeft(a, 14);
        c ^= b; c -= BitOperations.RotateLeft(b, 24);
    }
}
