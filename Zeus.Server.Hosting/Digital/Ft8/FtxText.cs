// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// Character tables and token helpers, ported from ft8_lib's ft8/text.c
// (Karlis Goba, MIT; see LICENSE.ft8_lib). The pack/unpack code relies on C
// string semantics — a buffer ends at its first NUL, reading past the end of a
// string yields NUL — so At() stands in for str[i] and tokens keep the C
// buffer limits.

using System.Text;

namespace Zeus.Server.Hosting.Digital.Ft8;

internal enum FtxCharTable
{
    Full,                // " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ+-./?"
    AlphanumSpaceSlash,  // " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ/"
    AlphanumSpace,       // " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"
    LettersSpace,        // " ABCDEFGHIJKLMNOPQRSTUVWXYZ"
    Alphanum,            // "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"
    Numeric,             // "0123456789"
}

internal static class FtxText
{
    /// <summary>s[i] as C reads it: NUL at and past the end.</summary>
    public static char At(string s, int i) => i >= 0 && i < s.Length ? s[i] : '\0';

    /// <summary>The C string a buffer holds: everything before the first NUL.</summary>
    public static string CStr(string s)
    {
        int nul = s.IndexOf('\0');
        return nul < 0 ? s : s[..nul];
    }

    public static bool IsDigit(char c) => c >= '0' && c <= '9';
    public static bool IsLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
    public static bool IsSpace(char c) => c == ' ';
    public static bool InRange(char c, char min, char max) => c >= min && c <= max;

    /// <summary>copy_token(): the next space-delimited token, cut to
    /// <paramref name="length"/> - 1 characters (the C buffer size, NUL
    /// included), and the position after the spaces that follow it. An
    /// over-long token is truncated, never reported.</summary>
    public static string CopyToken(string s, ref int pos, int length)
    {
        var token = new StringBuilder();
        while (pos < s.Length && s[pos] != ' ')
        {
            if (length > 1)
            {
                token.Append(s[pos]);
                length--;
            }
            pos++;
        }
        while (pos < s.Length && s[pos] == ' ') pos++;
        return token.ToString();
    }

    /// <summary>trim_copy(): strip leading and trailing spaces.</summary>
    public static string TrimSpaces(string s) => s.Trim(' ');

    /// <summary>dd_to_int(): an optionally signed integer from at most
    /// <paramref name="length"/> characters; stops at the first non-digit.</summary>
    public static int DdToInt(string str, int length)
    {
        int result = 0;
        bool negative;
        int i;
        if (At(str, 0) == '-')
        {
            negative = true;
            i = 1;
        }
        else
        {
            negative = false;
            i = At(str, 0) == '+' ? 1 : 0;
        }

        while (i < length)
        {
            char c = At(str, i);
            if (c == '\0' || !IsDigit(c)) break;
            result = result * 10 + (c - '0');
            ++i;
        }
        return negative ? -result : result;
    }

    /// <summary>int_to_dd(): sign (or '+' when <paramref name="fullSign"/>)
    /// then <paramref name="width"/> digits. Out-of-range values give the C's
    /// characters past '9', as it does.</summary>
    public static string IntToDd(int value, int width, bool fullSign)
    {
        var sb = new StringBuilder();
        if (value < 0)
        {
            sb.Append('-');
            value = -value;
        }
        else if (fullSign)
        {
            sb.Append('+');
        }

        int divisor = 1;
        for (int i = 0; i < width - 1; ++i) divisor *= 10;

        while (divisor >= 1)
        {
            int digit = value / divisor;
            sb.Append((char)('0' + digit));
            value -= digit * divisor;
            divisor /= 10;
        }
        return sb.ToString();
    }

    /// <summary>charn(): index → character in one of the tables.</summary>
    public static char Charn(int c, FtxCharTable table)
    {
        if (table != FtxCharTable.Alphanum && table != FtxCharTable.Numeric)
        {
            if (c == 0) return ' ';
            c -= 1;
        }
        if (table != FtxCharTable.LettersSpace)
        {
            if (c < 10) return (char)('0' + c);
            c -= 10;
        }
        if (table != FtxCharTable.Numeric)
        {
            if (c < 26) return (char)('A' + c);
            c -= 26;
        }

        if (table == FtxCharTable.Full)
        {
            if (c < 5) return "+-./?"[c];
        }
        else if (table == FtxCharTable.AlphanumSpaceSlash)
        {
            if (c == 0) return '/';
        }
        return '_';
    }

    /// <summary>nchar(): character → index in one of the tables, or -1.</summary>
    public static int Nchar(char c, FtxCharTable table)
    {
        int n = 0;
        if (table != FtxCharTable.Alphanum && table != FtxCharTable.Numeric)
        {
            if (c == ' ') return n;
            n += 1;
        }
        if (table != FtxCharTable.LettersSpace)
        {
            if (c >= '0' && c <= '9') return n + (c - '0');
            n += 10;
        }
        if (table != FtxCharTable.Numeric)
        {
            if (c >= 'A' && c <= 'Z') return n + (c - 'A');
            n += 26;
        }

        if (table == FtxCharTable.Full)
        {
            switch (c)
            {
                case '+': return n + 0;
                case '-': return n + 1;
                case '.': return n + 2;
                case '/': return n + 3;
                case '?': return n + 4;
            }
        }
        else if (table == FtxCharTable.AlphanumSpaceSlash)
        {
            if (c == '/') return n;
        }
        return -1;
    }
}
