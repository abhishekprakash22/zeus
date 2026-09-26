// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// WSPR message unpacking, ported from wsprd's wsprd_utils.c (unpack50,
// unpackcall, unpackgrid, unpackpfx, deinterleave, unpk_). The output
// strings are formatted exactly as the C formats them — "%02d" power for
// type 1, "%2d" (space-padded) for types 2 and 3, 22-character message cap —
// because they are what gets reported and de-duplicated.
//
// The callsign hash table lives in a WsprHashTable instance instead of a
// static C buffer: type-1 and type-2 decodes fill it, type-3 decodes look
// the hashed call up in it ("<...>" when unknown).
//
// Originals: Copyright 2001-2015 K1JT, K9AN; 2016 VA2GKA. GPL.

namespace Zeus.Server.Hosting.Digital.Wspr;

/// <summary>Calls heard as type 1/2, indexed by their 15-bit nhash, so a
/// later type-3 (hashed) message can name its sender.</summary>
public sealed class WsprHashTable
{
    public const int Size = 32768;
    private readonly string?[] _calls = new string?[Size];

    public void Put(string call)
    {
        // snprintf(hashtab + ihash*13, 13, "%s", call): at most 12 characters.
        _calls[Lookup3.Nhash(WsprEncoder.Ascii(call), 146)] = call.Length > 12 ? call[..12] : call;
    }

    public string? Get(int ihash) => (uint)ihash < Size ? _calls[ihash] : null;
}

internal readonly record struct WsprMessage(
    bool NoPrint, string CallLocPow, string Call, string Loc, string Pwr, string Callsign);

internal static class WsprUnpack
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ ";

    /// <summary>deinterleave(): inverse of the encoder's bit-reversal interleave.</summary>
    public static void Deinterleave(Span<byte> sym)
    {
        Span<byte> tmp = stackalloc byte[WsprEncoder.SymbolCount];
        int p = 0;
        for (int i = 0; p < WsprEncoder.SymbolCount; i = (i + 1) & 0xFF)
        {
            int j = WsprEncoder.ReverseByte(i);
            if (j < WsprEncoder.SymbolCount) tmp[p++] = sym[j];
        }
        tmp.CopyTo(sym);
    }

    public static void Unpack50(ReadOnlySpan<byte> dat, out int n1, out int n2)
    {
        n1 = dat[0] << 20;
        n1 += dat[1] << 12;
        n1 += dat[2] << 4;
        n1 += (dat[3] >> 4) & 15;
        n2 = (dat[3] & 15) << 18;
        n2 += dat[4] << 10;
        n2 += dat[5] << 2;
        n2 += (dat[6] >> 6) & 3;
    }

    public static bool UnpackCall(int ncall, out string call)
    {
        call = "......";
        if (ncall >= 262177560) return false;
        Span<char> tmp = stackalloc char[6];
        int n = ncall;
        tmp[5] = Alphabet[n % 27 + 10]; n /= 27;
        tmp[4] = Alphabet[n % 27 + 10]; n /= 27;
        tmp[3] = Alphabet[n % 27 + 10]; n /= 27;
        tmp[2] = Alphabet[n % 10]; n /= 10;
        tmp[1] = Alphabet[n % 36]; n /= 36;
        tmp[0] = Alphabet[n];
        int lead = 0;
        while (lead < 5 && tmp[lead] == ' ') lead++;                 // leading spaces
        string s = new string(tmp[lead..]);
        int sp = s.IndexOf(' ');                                     // trailing: cut at first space
        call = sp < 0 ? s : s[..sp];
        return true;
    }

    public static bool UnpackGrid(int ngrid, out string grid)
    {
        ngrid >>= 7;
        if (ngrid >= 32400) { grid = "XXXX"; return false; }
        int dlat = ngrid % 180 - 90;
        int dlong = ngrid / 180 * 2 - 180 + 2;
        if (dlong < -180) dlong += 360;
        if (dlong > 180) dlong += 360;                                 // (sic) as in the C
        int nlong = (int)(60.0 * (180.0 - dlong) / 5.0);
        int n1 = nlong / 240, n2 = (nlong - 240 * n1) / 24;
        char g0 = Alphabet[10 + n1], g2 = Alphabet[n2];
        int nlat = (int)(60.0 * (dlat + 90) / 2.5);
        n1 = nlat / 240;
        n2 = (nlat - 240 * n1) / 24;
        grid = new string([g0, Alphabet[10 + n1], g2, Alphabet[n2]]);
        return true;
    }

    public static bool UnpackPfx(int nprefix, ref string call)
    {
        string tmpcall = call;
        if (nprefix < 60000)
        {
            Span<char> pfx = stackalloc char[3];
            int n = nprefix;
            for (int i = 2; i >= 0; i--)
            {
                int nc = n % 37;
                pfx[i] = nc >= 0 && nc <= 9 ? (char)(nc + 48) : nc >= 10 && nc <= 35 ? (char)(nc + 55) : ' ';
                n /= 37;
            }
            string p = new string(pfx);
            int last = p.LastIndexOf(' ');
            call = Cap12($"{(last >= 0 ? p[(last + 1)..] : p)}/{tmpcall}");
            return true;
        }
        // Suffix: the C holds nprefix - 60000 in a (signed) char.
        int ncs = unchecked((sbyte)(nprefix - 60000));
        if (ncs >= 0 && ncs <= 9) call = Cap12($"{tmpcall}/{(char)(ncs + 48)}");
        else if (ncs >= 10 && ncs <= 35) call = Cap12($"{tmpcall}/{(char)(ncs + 55)}");
        else if (ncs >= 36 && ncs <= 125)
            call = Cap12($"{tmpcall}/{(char)((ncs - 26) / 10 + 48)}{(char)((ncs - 26) % 10 + 48)}");
        else return false;
        return true;
    }

    /// <summary>unpk_(): 11 decoded bytes → message strings. NoPrint marks
    /// decodes the C refuses to report (odd powers, implausible grids).</summary>
    public static WsprMessage Unpack(ReadOnlySpan<byte> message, WsprHashTable hashtab)
    {
        Unpack50(message, out int n1, out int n2);
        if (!UnpackCall(n1, out string callsign)) return Refused();
        if (!UnpackGrid(n2, out string grid)) return Refused();
        int ntype = (n2 & 127) - 64;

        if (ntype >= 0 && ntype <= 62)
        {
            int nu = ntype % 10;
            if (nu == 0 || nu == 3 || nu == 7)
            {
                // Type 1.
                string cdbm = ntype.ToString("00");
                hashtab.Put(callsign);
                return new(false, Cap22($"{callsign} {grid} {cdbm}"), Cap12(callsign), grid, Cap2(cdbm), callsign);
            }
            // Type 2: prefix/suffix call.
            int nadd = nu;
            if (nu > 3) nadd = nu - 3;
            if (nu > 7) nadd = nu - 7;
            int n3 = n2 / 128 + WsprHashTable.Size * (nadd - 1);
            if (!UnpackPfx(n3, ref callsign)) return Refused(callsign);
            int ndbm = ntype - nadd;
            string cdbm2 = ndbm.ToString().PadLeft(2);
            bool noprint = false;
            int nu2 = ndbm % 10;
            if (nu2 == 0 || nu2 == 3 || nu2 == 7) hashtab.Put(callsign);
            else noprint = true;
            // (the C leaves call/loc/pwr untouched for type 2 — empty here)
            return new(noprint, Cap22($"{callsign} {cdbm2}"), "", "", "", callsign);
        }
        if (ntype < 0)
        {
            // Type 3: hashed call + 6-char grid rebuilt from the "call" field.
            int ndbm = -(ntype + 1);
            // snprintf("%c%.*s", callsign[5], 5, callsign) — a call shorter
            // than 6 puts a NUL first, i.e. an empty C string.
            string grid6 = callsign.Length >= 6 ? callsign[5] + callsign[..5] : "";
            int nu = ndbm % 10;
            bool noprint = (nu != 0 && nu != 3 && nu != 7)
                           || !(IsAlpha(At(grid6, 0)) && IsAlpha(At(grid6, 1))
                                && IsDigit(At(grid6, 2)) && IsDigit(At(grid6, 3)));
            int ihash = (n2 - ntype - 64) / 128;
            string? known = hashtab.Get(ihash);
            string call = Cap12(!string.IsNullOrEmpty(known) ? $"<{known}>" : "<...>");
            string cdbm = ndbm.ToString().PadLeft(2);
            if (ntype == -64) noprint = true;                        // "A000AA" grids
            return new(noprint, Cap22($"{call} {grid6} {cdbm}"), call, grid6, Cap2(cdbm), call);
        }
        // ntype 63: no message type — nothing to report (wsprd returned it
        // unrefused, with an empty message).
        return Refused(callsign);

        static WsprMessage Refused(string callsign = "") => new(true, "", "", "", "", callsign);
    }

    private static char At(string s, int i) => i < s.Length ? s[i] : '\0';
    private static bool IsAlpha(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    private static bool IsDigit(char c) => c is >= '0' and <= '9';
    private static string Cap(string s, int n) => s.Length > n ? s[..n] : s;
    private static string Cap22(string s) => Cap(s, 22);
    private static string Cap12(string s) => Cap(s, 12);
    private static string Cap2(string s) => Cap(s, 2);
}
