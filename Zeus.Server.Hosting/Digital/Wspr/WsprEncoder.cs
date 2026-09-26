// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// WSPR channel-symbol encoder, ported from wsprd's wsprsim_utils.c
// (get_wspr_channel_symbols and helpers) and fano.c's encode(). Output is
// the 162 symbols (0..3) the beacon keys — it must match WSJT-X bit for
// bit, so this follows the C line for line, C-string semantics included
// (a callsign ends at the first NUL, out-of-alphabet characters code as -1),
// and SstvTests-style golden tests pin it against the native library.
//
// Message types (chosen, as in the C, by the presence of '<' and '/'):
//   1  "K1ABC FN42 37"        call (≤6) + 4-char grid + dBm
//   2  "PJ4/K1ABC 37"         prefix or suffix call + dBm
//   3  "<K1ABC> FN42AB 37"    hashed call + 6-char grid + dBm
//
// Originals: Copyright 2001-2015 Joe Taylor K1JT; Steven Franke K9AN;
// Phil Karn KA9Q (convolutional code); Guenael Jouchet VA2GKA. GPL.

using System.Numerics;

namespace Zeus.Server.Hosting.Digital.Wspr;

public static class WsprEncoder
{
    public const int SymbolCount = 162;

    // Layland-Lushbaugh K=32 r=1/2 convolutional code (fano.c, LL).
    internal const uint Poly1 = 0xf2d05351;
    internal const uint Poly2 = 0xe4613c47;

    /// <summary>The 162-bit sync vector merged into the low bit of every symbol.</summary>
    internal static ReadOnlySpan<byte> Sync =>
    [
        1, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 0, 0, 0, 1, 0,
        0, 1, 0, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 1,
        0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 0, 0, 0, 1,
        1, 0, 1, 0, 0, 0, 0, 1, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 0, 1,
        0, 0, 1, 0, 1, 1, 0, 0, 0, 1, 1, 0, 1, 0, 1, 0, 0, 0, 1, 0,
        0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 1, 1, 1, 0, 1, 1, 0, 0, 1, 1,
        0, 1, 0, 0, 0, 1, 1, 1, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 1, 1,
        0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 1, 0, 1, 1, 0, 0, 0, 1, 1, 0,
        0, 0,
    ];

    private static readonly int[] Nu = [0, -1, 1, 0, -1, 2, 1, 0, -1, 1];

    /// <summary>
    /// "CALL GRID DBM" (type 1), "PFX/CALL DBM" (type 2) or "&lt;CALL&gt; GRID6 DBM"
    /// (type 3) → 162 channel symbols. False when the message has no shape the
    /// C would encode (it would crash or return 0 there).
    /// </summary>
    public static bool TryEncode(string rawMessage, Span<byte> symbols)
    {
        if (symbols.Length < SymbolCount) return false;

        // message[23]: at most 23 chars, as the C copies them.
        string message = rawMessage.Length > 23 ? rawMessage[..23] : rawMessage;
        int nul = message.IndexOf('\0');
        if (nul >= 0) message = message[..nul];

        int mlen = message.Length;
        int i1 = CSpan(message, ' '), i2 = CSpan(message, '/');
        int i3 = CSpan(message, '<'), i4 = CSpan(message, '>');

        long n;
        int m;
        if (i1 > 3 && i1 < 7 && i2 == mlen && i3 == mlen)
        {
            // Type 1: K9AN EN50 33 — no power clamping or rounding here.
            var tok = Tokens(message, " ");
            if (tok.Count < 3 || tok[1].Length < 4) return false;
            int power = Atoi(tok[2]);
            n = PackCall(tok[0]);
            Span<int> g = stackalloc int[4];
            for (int i = 0; i < 4; i++) g[i] = LocatorCode(tok[1][i]);
            m = (int)PackGrid4Power(g, power);
        }
        else if (i3 == 0 && i4 < mlen)
        {
            // Type 3: <K1ABC> EN50WC 33 — the call's hash stands in for it,
            // making room for a 6-character grid.
            var tok = Tokens(message, "<> ");
            if (tok.Count < 3) return false;
            string callsign = tok[0];
            // strtok(NULL, " ") after "<> ": the grid and power tokens.
            var rest = Tokens(After(message, callsign), " ");
            if (rest.Count < 2) return false;
            string grid = rest[0];
            int power = Math.Clamp(Atoi(rest[1]), 0, 60);
            power += Nu[power % 10];
            int ntype = -(power + 1);
            int ihash = Lookup3.Nhash(Ascii(callsign), 146);
            m = unchecked(128 * ihash + ntype + 64);

            // grid6 = grid[1..] + grid[0], as a C string in char[7].
            Span<char> grid6 = stackalloc char[7];
            grid6.Clear();
            int j = grid.Length;
            for (int i = 0; i < j - 1 && i < 6; i++) grid6[i] = grid[i + 1];
            if (j > 0) grid6[5] = grid[0];
            n = PackCall(CString(grid6));
        }
        else if (i2 < mlen)
        {
            // Type 2: PJ4/K1ABC 37 — prefix or suffix, no grid.
            var tok = Tokens(message, " ");
            if (tok.Count < 2) return false;
            string callsign = tok[0];
            if (i2 == 0 || i2 > callsign.Length) return false;   // the C's own guard
            int power = Math.Clamp(Atoi(tok[1]), 0, 60);
            power += Nu[power % 10];
            PackPrefix(callsign, out int n1, out int ng, out int nadd);
            int ntype = power + 1 + nadd;
            m = 128 * ng + ntype + 64;
            n = n1;
        }
        else
        {
            return false;
        }

        // Pack 50 bits (+ 31 zero tail bits) into 11 bytes.
        Span<byte> data = stackalloc byte[11];
        data.Clear();
        data[0] = (byte)(n >> 20);
        data[1] = (byte)(n >> 12);
        data[2] = (byte)(n >> 4);
        data[3] = (byte)(((n & 0x0F) << 4) + ((m >> 18) & 0x0F));
        data[4] = (byte)(m >> 10);
        data[5] = (byte)(m >> 2);
        data[6] = (byte)((m & 0x03) << 6);

        Span<byte> channelBits = stackalloc byte[11 * 8 * 2];
        ConvolutionalEncode(data, channelBits);
        Interleave(channelBits[..SymbolCount]);

        var sync = Sync;
        for (int i = 0; i < SymbolCount; i++)
            symbols[i] = (byte)(2 * channelBits[i] + sync[i]);
        return true;
    }

    // ---- packing ------------------------------------------------------------

    /// <summary>pack_call(): six characters, digit in position 2 (a leading
    /// space is inserted when the digit is in position 1).</summary>
    internal static long PackCall(string callsign)
    {
        if (callsign.Length > 6) return 0;
        Span<int> c6 = stackalloc int[6];
        Span<char> call6 = stackalloc char[6];
        call6.Fill(' ');
        if (IsDigit(At(callsign, 2)))
            for (int i = 0; i < callsign.Length; i++) call6[i] = callsign[i];
        else if (IsDigit(At(callsign, 1)))
            for (int i = 1; i < callsign.Length + 1; i++) call6[i] = callsign[i - 1];
        for (int i = 0; i < 6; i++) c6[i] = CallsignCode(call6[i]);

        long n = c6[0];
        n = n * 36 + c6[1];
        n = n * 10 + c6[2];
        n = n * 27 + c6[3] - 10;
        n = n * 27 + c6[4] - 10;
        n = n * 27 + c6[5] - 10;
        return n;
    }

    private static long PackGrid4Power(ReadOnlySpan<int> g, int power)
    {
        long m = (179 - 10 * g[0] - g[2]) * 180L + 10 * g[1] + g[3];
        return m * 128 + power + 64;
    }

    /// <summary>pack_prefix(): 1- or 2-character suffix, or a prefix of up
    /// to 3 characters, folded into (n, m, nadd).</summary>
    private static void PackPrefix(string callsign, out int n, out int m, out int nadd)
    {
        int i1 = CSpan(callsign, '/');
        if (At(callsign, i1 + 2) == '\0')
        {
            // single-character suffix: K1ABC/P
            n = (int)PackCall(callsign[..i1]);
            nadd = 1;
            char nc = At(callsign, i1 + 1);
            m = nc >= '0' && nc <= '9' ? nc - '0'
              : nc >= 'A' && nc <= 'Z' ? nc - 'A' + 10
              : 38;
            m = 60000 - 32768 + m;
        }
        else if (At(callsign, i1 + 3) == '\0')
        {
            // two-digit suffix: K1ABC/12
            n = (int)PackCall(callsign[..i1]);
            nadd = 1;
            m = 10 * (At(callsign, i1 + 1) - '0') + (At(callsign, i1 + 2) - '0');
            m = 60000 + 26 + m;
        }
        else
        {
            // prefix: PJ4/K1ABC
            string pfx = callsign[..i1];
            string call = callsign[(i1 + 1)..];
            n = (int)PackCall(call);
            int plen = pfx.Length;
            m = plen == 1 ? 37 * 36 + 36 : plen == 2 ? 36 : 0;
            for (int i = 0; i < plen; i++)
            {
                int nc = pfx[i];
                nc = nc >= '0' && nc <= '9' ? nc - '0'
                   : nc >= 'A' && nc <= 'Z' ? nc - 'A' + 10
                   : 36;
                m = 37 * m + nc;
            }
            nadd = 0;
            if (m > 32768) { m -= 32768; nadd = 1; }
        }
    }

    /// <summary>fano.c encode(): K=32 r=1/2, bytes read high bit first, one
    /// symbol per output byte (POLY1 first).</summary>
    internal static void ConvolutionalEncode(ReadOnlySpan<byte> data, Span<byte> symbols)
    {
        uint encstate = 0;
        int o = 0;
        foreach (byte d in data)
            for (int i = 7; i >= 0; i--)
            {
                encstate = (encstate << 1) | (uint)((d >> i) & 1);
                symbols[o++] = Parity(encstate & Poly1);
                symbols[o++] = Parity(encstate & Poly2);
            }
    }

    internal static byte Parity(uint x) => (byte)(BitOperations.PopCount(x) & 1);

    /// <summary>Bit-reversal interleave over 162 symbols (wsprsim interleave()).</summary>
    internal static void Interleave(Span<byte> sym)
    {
        Span<byte> tmp = stackalloc byte[SymbolCount];
        int p = 0;
        for (int i = 0; p < SymbolCount; i = (i + 1) & 0xFF)
        {
            int j = ReverseByte(i);
            if (j < SymbolCount) tmp[j] = sym[p++];
        }
        tmp.CopyTo(sym);
    }

    internal static int ReverseByte(int b)
    {
        int r = 0;
        for (int k = 0; k < 8; k++) r |= ((b >> k) & 1) << (7 - k);
        return r;
    }

    // ---- C-string helpers -------------------------------------------------

    private static int CallsignCode(char ch) =>
        ch >= '0' && ch <= '9' ? ch - '0'
        : ch == ' ' ? 36
        : ch >= 'A' && ch <= 'Z' ? ch - 55
        : -1;

    private static int LocatorCode(char ch) =>
        ch >= '0' && ch <= '9' ? ch - '0'
        : ch == ' ' ? 36
        : ch >= 'A' && ch <= 'R' ? ch - 'A'
        : -1;

    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    private static char At(string s, int i) => i >= 0 && i < s.Length ? s[i] : '\0';

    /// <summary>strcspn for one reject character.</summary>
    private static int CSpan(string s, char reject)
    {
        int i = s.IndexOf(reject);
        return i < 0 ? s.Length : i;
    }

    /// <summary>strtok-style tokens (runs of delimiters skipped).</summary>
    private static List<string> Tokens(string s, string delims) =>
        [.. s.Split(delims.ToCharArray(), StringSplitOptions.RemoveEmptyEntries)];

    private static string After(string s, string token)
    {
        int i = s.IndexOf(token, StringComparison.Ordinal);
        return i < 0 ? "" : s[(i + token.Length)..].TrimStart('<', '>', ' ');
    }

    private static string CString(ReadOnlySpan<char> buf)
    {
        int nul = buf.IndexOf('\0');
        return new string(nul < 0 ? buf : buf[..nul]);
    }

    internal static byte[] Ascii(string s)
    {
        var b = new byte[s.Length];
        for (int i = 0; i < s.Length; i++) b[i] = (byte)s[i];
        return b;
    }

    /// <summary>atoi(): optional sign and leading digits, else 0.</summary>
    private static int Atoi(string s)
    {
        int i = 0, sign = 1, v = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        if (i < s.Length && (s[i] == '+' || s[i] == '-')) { if (s[i] == '-') sign = -1; i++; }
        while (i < s.Length && s[i] >= '0' && s[i] <= '9') { v = v * 10 + (s[i] - '0'); i++; }
        return sign * v;
    }
}
