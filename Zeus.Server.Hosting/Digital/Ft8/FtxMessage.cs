// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// FT8/FT4 message text ⇄ 77-bit payload, ported from ft8_lib's ft8/message.c
// (Karlis Goba, MIT; non-standard calls originally by KD8CEC; see
// LICENSE.ft8_lib). Handles the types ft8_lib does:
//
//   0.0  free text, up to 13 characters
//   0.5  telemetry (decode only)
//   1/2  standard: two calls (optionally /R or /P) + grid, report or token
//   4    one non-standard call sent in full + one hashed call
//
// This follows the C line for line, quirks included, because the golden
// tests hold it to the native encoder's output (TestData/ft8). The known
// quirks are listed in docs/designs/ft8-managed-port.md; fixing them is
// separate work.

using System.Text;
using static Zeus.Server.Hosting.Digital.Ft8.FtxText;

namespace Zeus.Server.Hosting.Digital.Ft8;

public enum FtxMessageRc
{
    Ok,
    ErrorCallsign1,
    ErrorCallsign2,
    ErrorSuffix,
    ErrorGrid,
    ErrorType,
}

public enum FtxHashType
{
    Bits22,
    Bits12,
    Bits10,
}

/// <summary>ft8_lib's callsign hash interface: every callsign seen is saved by
/// its 22-bit hash, and hashed calls in later messages are looked up by their
/// 22-, 12- or 10-bit form.</summary>
public interface IFtxCallsignHash
{
    bool Lookup(FtxHashType type, uint hash, out string callsign);
    void Save(string callsign, uint n22);
}

public static class FtxMessage
{
    public const int PayloadBytes = 10;

    private const uint Max22 = 4194304;
    private const uint NTokens = 2063592;
    private const ushort MaxGrid4 = 32400;

    // ---- encode -------------------------------------------------------------

    /// <summary>ftx_message_encode(): pack a message, trying a standard
    /// message, then a non-standard-call one, then free text.</summary>
    public static FtxMessageRc Encode(string text, IFtxCallsignHash? hash, Span<byte> payload)
    {
        text = CStr(text);
        string callTo, callDe, extra;

        int pos = 0;
        bool isCq = text.StartsWith("CQ ", StringComparison.Ordinal);
        if (isCq)
        {
            pos = 3;
            // "CQ nnn" / "CQ a[bcd]" is one token; checked on the whole text, as the C does.
            if (ParseCqModifier(text) >= 0)
                callTo = "CQ " + CopyToken(text, ref pos, 12 - 3);
            else
                callTo = "CQ";
        }
        else
        {
            callTo = CopyToken(text, ref pos, 12);
        }
        callDe = CopyToken(text, ref pos, 12);
        extra = CopyToken(text, ref pos, 20);
        // The C's "token too long" checks can never fire: copy_token truncates.

        FtxMessageRc rc;
        if (pos >= text.Length)
        {
            rc = EncodeStd(callTo, callDe, extra, hash, payload);
            if (rc == FtxMessageRc.Ok) return rc;
            rc = EncodeNonstd(callTo, callDe, extra, hash, payload);
            if (rc == FtxMessageRc.Ok) return rc;
        }
        return EncodeFree(text, payload);
    }

    /// <summary>ftx_message_encode_std(): type 1 or 2.</summary>
    internal static FtxMessageRc EncodeStd(string callTo, string callDe, string extra,
                                           IFtxCallsignHash? hash, Span<byte> payload)
    {
        int n28a = Pack28(callTo, hash, out byte ipa);
        int n28b = Pack28(callDe, hash, out byte ipb);
        if (n28a < 0) return FtxMessageRc.ErrorCallsign1;
        if (n28b < 0) return FtxMessageRc.ErrorCallsign2;

        byte i3 = 1;                                  // no suffix, or /R
        if (callTo.EndsWith("/P", StringComparison.Ordinal) || callDe.EndsWith("/P", StringComparison.Ordinal))
        {
            i3 = 2;                                   // /P: EU VHF contest
            if (callTo.EndsWith("/R", StringComparison.Ordinal) || callDe.EndsWith("/R", StringComparison.Ordinal))
                return FtxMessageRc.ErrorSuffix;
        }

        int slashDe = callDe.IndexOf('/');
        bool icq = callTo == "CQ" || callTo.StartsWith("CQ ", StringComparison.Ordinal);
        if (slashDe >= 2 && icq && !(callDe[slashDe..] == "/P" || callDe[slashDe..] == "/R"))
            return FtxMessageRc.ErrorCallsign2;       // non-standard call: needs type 4

        ushort igrid4 = PackGrid(extra);

        uint n29a = ((uint)n28a << 1) | ipa;
        uint n29b = ((uint)n28b << 1) | ipb;
        if (callTo.EndsWith("/R", StringComparison.Ordinal))
        {
            n29a |= 1;
        }
        else if (callTo.EndsWith("/P", StringComparison.Ordinal))
        {
            n29a |= 1;
            i3 = 2;
        }

        payload[0] = (byte)(n29a >> 21);
        payload[1] = (byte)(n29a >> 13);
        payload[2] = (byte)(n29a >> 5);
        payload[3] = (byte)((byte)(n29a << 3) | (byte)(n29b >> 26));
        payload[4] = (byte)(n29b >> 18);
        payload[5] = (byte)(n29b >> 10);
        payload[6] = (byte)(n29b >> 2);
        payload[7] = (byte)((byte)(n29b << 6) | (byte)(igrid4 >> 10));
        payload[8] = (byte)(igrid4 >> 2);
        payload[9] = (byte)((byte)(igrid4 << 6) | (byte)(i3 << 3));
        return FtxMessageRc.Ok;
    }

    /// <summary>ftx_message_encode_nonstd(): type 4.</summary>
    internal static FtxMessageRc EncodeNonstd(string callTo, string callDe, string extra,
                                              IFtxCallsignHash? hash, Span<byte> payload)
    {
        const byte i3 = 4;
        bool icq = callTo == "CQ" || callTo.StartsWith("CQ ", StringComparison.Ordinal);
        int lenCallTo = callTo.Length;
        int lenCallDe = callDe.Length;

        if (!icq && lenCallTo < 3) return FtxMessageRc.ErrorCallsign1;
        if (lenCallDe < 3) return FtxMessageRc.ErrorCallsign2;

        byte iflip;
        ushort n12;
        string call58;
        if (!icq)
        {
            // Which call goes in full (58 bits) and which as a 12-bit hash. The C
            // indexes call_de with call_TO's length here; kept as the C.
            iflip = 0;
            if (At(callDe, 0) == '<' && At(callDe, lenCallTo - 1) == '>') iflip = 1;

            string call12 = iflip == 0 ? callTo : callDe;
            call58 = iflip == 0 ? callDe : callTo;
            if (!SaveCallsign(hash, call12, out uint n22)) return FtxMessageRc.ErrorCallsign1;
            n12 = (ushort)(n22 >> 10);
        }
        else
        {
            iflip = 0;
            n12 = 0;
            call58 = callDe;
        }

        if (!Pack58(hash, call58, out ulong n58)) return FtxMessageRc.ErrorCallsign2;

        byte nrpt = icq ? (byte)0
            : extra == "RRR" ? (byte)1
            : extra == "RR73" ? (byte)2
            : extra == "73" ? (byte)3
            : (byte)0;
        byte bIcq = icq ? (byte)1 : (byte)0;

        payload[0] = (byte)(n12 >> 4);
        payload[1] = (byte)((byte)(n12 << 4) | (byte)(n58 >> 54));
        payload[2] = (byte)(n58 >> 46);
        payload[3] = (byte)(n58 >> 38);
        payload[4] = (byte)(n58 >> 30);
        payload[5] = (byte)(n58 >> 22);
        payload[6] = (byte)(n58 >> 14);
        payload[7] = (byte)(n58 >> 6);
        payload[8] = (byte)((byte)(n58 << 2) | (byte)(iflip << 1) | (byte)(nrpt >> 1));
        payload[9] = (byte)((byte)(nrpt << 7) | (byte)(bIcq << 6) | (byte)(i3 << 3));
        return FtxMessageRc.Ok;
    }

    /// <summary>ftx_message_encode_free(): up to 13 characters of the Full table.</summary>
    internal static FtxMessageRc EncodeFree(string text, Span<byte> payload)
    {
        byte strLen = (byte)text.Length;              // uint8_t in the C
        if (strLen > 13) return FtxMessageRc.ErrorType;

        Span<byte> b71 = stackalloc byte[9];
        for (int idx = 0; idx < 13; idx++)
        {
            char c = idx < strLen ? text[idx] : ' ';
            int cid = Nchar(c, FtxCharTable.Full);
            if (cid == -1) return FtxMessageRc.ErrorType;

            int rem = cid;
            for (int i = 8; i >= 0; i--)
            {
                rem += b71[i] * 42;
                b71[i] = (byte)(rem & 0xff);
                rem >>= 8;
            }
        }

        // ftx_message_encode_telemetry(): shift left one bit to right-align.
        int carry = 0;
        for (int i = 8; i >= 0; --i)
        {
            payload[i] = (byte)((b71[i] << 1) | (carry >> 7));
            carry = b71[i] & 0x80;
        }
        payload[9] = 0;                               // i3.n3 = 0.0
        return FtxMessageRc.Ok;
    }

    // ---- decode -------------------------------------------------------------

    /// <summary>ftx_message_decode(): payload → message text. The text is only
    /// meaningful when the result is <see cref="FtxMessageRc.Ok"/>.</summary>
    public static FtxMessageRc Decode(ReadOnlySpan<byte> payload, IFtxCallsignHash? hash, out string message)
    {
        int i3 = (payload[9] >> 3) & 0x07;
        int n3 = ((payload[8] << 2) & 0x04) | ((payload[9] >> 6) & 0x03);

        string? f1, f2 = null, f3 = null;
        FtxMessageRc rc;
        if (i3 == 1 || i3 == 2)
        {
            rc = DecodeStd(payload, hash, out f1, out f2, out f3);
        }
        else if (i3 == 4)
        {
            rc = DecodeNonstd(payload, hash, out f1, out f2, out f3);
        }
        else if (i3 == 0 && n3 == 0)
        {
            f1 = DecodeFree(payload);
            rc = FtxMessageRc.Ok;
        }
        else if (i3 == 0 && n3 == 5)
        {
            f1 = DecodeTelemetryHex(payload);
            rc = FtxMessageRc.Ok;
        }
        else
        {
            f1 = null;
            rc = FtxMessageRc.ErrorType;
        }

        var sb = new StringBuilder();
        if (f1 != null)
        {
            sb.Append(f1);
            if (f2 != null)
            {
                sb.Append(' ').Append(f2);
                if (!string.IsNullOrEmpty(f3)) sb.Append(' ').Append(f3);
            }
        }
        message = sb.ToString();
        return rc;
    }

    private static FtxMessageRc DecodeStd(ReadOnlySpan<byte> p, IFtxCallsignHash? hash,
                                          out string callTo, out string callDe, out string extra)
    {
        uint n29a = ((uint)p[0] << 21) | ((uint)p[1] << 13) | ((uint)p[2] << 5) | ((uint)p[3] >> 3);
        uint n29b = ((uint)(p[3] & 0x07) << 26) | ((uint)p[4] << 18) | ((uint)p[5] << 10)
                  | ((uint)p[6] << 2) | ((uint)p[7] >> 6);
        byte ir = (byte)((p[7] & 0x20) >> 5);
        ushort igrid4 = (ushort)(((p[7] & 0x1F) << 10) | (p[8] << 2) | (p[9] >> 6));
        byte i3 = (byte)((p[9] >> 3) & 0x07);

        callTo = callDe = extra = "";
        if (Unpack28(n29a >> 1, (byte)(n29a & 1), i3, hash, out callTo) < 0) return FtxMessageRc.ErrorCallsign1;
        if (Unpack28(n29b >> 1, (byte)(n29b & 1), i3, hash, out callDe) < 0) return FtxMessageRc.ErrorCallsign2;
        extra = UnpackGrid(igrid4, ir);
        return FtxMessageRc.Ok;
    }

    private static FtxMessageRc DecodeNonstd(ReadOnlySpan<byte> p, IFtxCallsignHash? hash,
                                             out string callTo, out string callDe, out string extra)
    {
        uint n12 = (uint)((p[0] << 4) | (p[1] >> 4));
        ulong n58 = ((ulong)(p[1] & 0x0F) << 54) | ((ulong)p[2] << 46) | ((ulong)p[3] << 38)
                  | ((ulong)p[4] << 30) | ((ulong)p[5] << 22) | ((ulong)p[6] << 14)
                  | ((ulong)p[7] << 6) | ((ulong)p[8] >> 2);
        int iflip = (p[8] >> 1) & 0x01;
        int nrpt = ((p[8] & 0x01) << 1) | (p[9] >> 7);
        int icq = (p[9] >> 6) & 0x01;

        string callDecoded = Unpack58(n58, hash);
        string call3 = LookupCallsign(hash, FtxHashType.Bits12, n12);

        string call1 = iflip != 0 ? callDecoded : call3;
        string call2 = iflip != 0 ? call3 : callDecoded;

        if (icq == 0)
        {
            callTo = call1;
            extra = nrpt switch { 1 => "RRR", 2 => "RR73", 3 => "73", _ => "" };
        }
        else
        {
            callTo = "CQ";
            extra = "";
        }
        callDe = call2;
        return FtxMessageRc.Ok;
    }

    private static void DecodeTelemetry(ReadOnlySpan<byte> payload, Span<byte> telemetry)
    {
        int carry = 0;
        for (int i = 0; i < 9; ++i)
        {
            telemetry[i] = (byte)((carry << 7) | (payload[i] >> 1));
            carry = payload[i] & 0x01;
        }
    }

    private static string DecodeFree(ReadOnlySpan<byte> payload)
    {
        Span<byte> b71 = stackalloc byte[9];
        DecodeTelemetry(payload, b71);

        var c14 = new char[13];
        for (int idx = 12; idx >= 0; --idx)
        {
            int rem = 0;
            for (int i = 0; i < 9; ++i)
            {
                rem = ((rem << 8) | b71[i]) & 0xFFFF;
                b71[i] = (byte)(rem / 42);
                rem %= 42;
            }
            c14[idx] = Charn(rem, FtxCharTable.Full);
        }
        return new string(c14).Trim(' ');
    }

    private static string DecodeTelemetryHex(ReadOnlySpan<byte> payload)
    {
        Span<byte> b71 = stackalloc byte[9];
        DecodeTelemetry(payload, b71);
        var sb = new StringBuilder(18);
        for (int i = 0; i < 9; ++i) sb.Append(b71[i].ToString("X2"));
        return sb.ToString();
    }

    // ---- fields -------------------------------------------------------------

    /// <summary>save_callsign(): the 22-bit hash of a call (first 11 characters,
    /// space-padded), saved through <paramref name="hash"/>. False when a
    /// character is outside the alphanumeric/space/slash table.</summary>
    internal static bool SaveCallsign(IFtxCallsignHash? hash, string callsign, out uint n22)
    {
        n22 = 0;
        ulong n58 = 0;
        int i = 0;
        while (i < callsign.Length && callsign[i] != '\0' && i < 11)
        {
            int j = Nchar(callsign[i], FtxCharTable.AlphanumSpaceSlash);
            if (j < 0) return false;
            n58 = 38 * n58 + (ulong)j;
            i++;
        }
        while (i < 11)
        {
            n58 = 38 * n58;
            i++;
        }

        n22 = (uint)(unchecked(47055833459UL * n58) >> (64 - 22)) & 0x3FFFFF;
        hash?.Save(callsign, n22);
        return true;
    }

    private static string LookupCallsign(IFtxCallsignHash? hash, FtxHashType type, uint value)
    {
        if (hash != null && hash.Lookup(type, value, out string c11))
            return "<" + c11 + ">";
        return "<...>";
    }

    /// <summary>pack_basecall(): a standard base call as a 28-bit number, or -1.</summary>
    internal static int PackBasecall(string callsign, int length)
    {
        if (length <= 2) return -1;

        var c6 = new[] { ' ', ' ', ' ', ' ', ' ', ' ' };
        if (callsign.StartsWith("3DA0", StringComparison.Ordinal) && length > 4 && length <= 7)
        {
            // Swaziland: 3DA0XYZ -> 3D0XYZ
            c6[0] = '3'; c6[1] = 'D'; c6[2] = '0';
            for (int k = 0; k < length - 4; k++) c6[3 + k] = At(callsign, 4 + k);
        }
        else if (callsign.StartsWith("3X", StringComparison.Ordinal) && IsLetter(At(callsign, 2)) && length <= 7)
        {
            // Guinea: 3XA0XYZ -> QA0XYZ
            c6[0] = 'Q';
            for (int k = 0; k < length - 2; k++) c6[1 + k] = At(callsign, 2 + k);
        }
        else if (IsDigit(At(callsign, 2)) && length <= 6)
        {
            for (int k = 0; k < length; k++) c6[k] = At(callsign, k);          // AB0XYZ
        }
        else if (IsDigit(At(callsign, 1)) && length <= 5)
        {
            for (int k = 0; k < length; k++) c6[1 + k] = At(callsign, k);      // A0XYZ -> " A0XYZ"
        }

        int i0 = Nchar(c6[0], FtxCharTable.AlphanumSpace);
        int i1 = Nchar(c6[1], FtxCharTable.Alphanum);
        int i2 = Nchar(c6[2], FtxCharTable.Numeric);
        int i3 = Nchar(c6[3], FtxCharTable.LettersSpace);
        int i4 = Nchar(c6[4], FtxCharTable.LettersSpace);
        int i5 = Nchar(c6[5], FtxCharTable.LettersSpace);
        if (i0 < 0 || i1 < 0 || i2 < 0 || i3 < 0 || i4 < 0 || i5 < 0) return -1;

        int n = i0;
        n = n * 36 + i1;
        n = n * 10 + i2;
        n = n * 27 + i3;
        n = n * 27 + i4;
        n = n * 27 + i5;
        return n;
    }

    /// <summary>parse_cq_modifier(): the value of "CQ nnn" / "CQ a[bcd]" in
    /// <paramref name="s"/> (read from index 3), or -1.</summary>
    internal static int ParseCqModifier(string s)
    {
        int nnum = 0, nlet = 0, m = 0;
        for (int i = 3; i < 8; ++i)
        {
            char c = At(s, i);
            if (c == '\0' || IsSpace(c)) break;
            if (IsDigit(c)) ++nnum;
            else if (IsLetter(c))
            {
                ++nlet;
                m = 27 * m + (c - 'A' + 1);
            }
            else return -1;
        }
        if (nnum == 3 && nlet == 0) return Atoi(s, 3);
        if (nnum == 0 && nlet <= 4) return 1000 + m;
        return -1;
    }

    /// <summary>atoi(): optional spaces and sign, then digits.</summary>
    private static int Atoi(string s, int i)
    {
        while (At(s, i) == ' ') i++;
        bool neg = false;
        if (At(s, i) == '-' || At(s, i) == '+') neg = s[i++] == '-';
        int v = 0;
        while (IsDigit(At(s, i))) v = v * 10 + (s[i++] - '0');
        return neg ? -v : v;
    }

    /// <summary>pack28(): a token, a 22-bit hash or a base call as a 28-bit
    /// number (-1 on error); <paramref name="ip"/> is the /R or /P flag.</summary>
    internal static int Pack28(string callsign, IFtxCallsignHash? hash, out byte ip)
    {
        ip = 0;
        if (callsign == "DE") return 0;
        if (callsign == "QRZ") return 1;
        if (callsign == "CQ") return 2;

        int length = callsign.Length;
        if (callsign.StartsWith("CQ ", StringComparison.Ordinal) && length < 8)
        {
            int v = ParseCqModifier(callsign);
            return v < 0 ? -1 : 3 + v;
        }

        int lengthBase = length;
        if (callsign.EndsWith("/P", StringComparison.Ordinal) || callsign.EndsWith("/R", StringComparison.Ordinal))
        {
            ip = 1;
            lengthBase = length - 2;
        }

        int n28 = PackBasecall(callsign, lengthBase);
        if (n28 >= 0)
        {
            if (!SaveCallsign(hash, callsign, out _)) return -1;
            return (int)(NTokens + Max22 + (uint)n28);
        }

        if (length >= 3 && length <= 11)
        {
            if (!SaveCallsign(hash, callsign, out uint n22)) return -1;
            ip = 0;
            return (int)(NTokens + n22);
        }
        return -1;
    }

    /// <summary>unpack28(): the inverse of Pack28. 0 on success, negative on error.</summary>
    private static int Unpack28(uint n28, byte ip, byte i3, IFtxCallsignHash? hash, out string result)
    {
        result = "";
        if (n28 < NTokens)
        {
            if (n28 <= 2)
            {
                result = n28 == 0 ? "DE" : n28 == 1 ? "QRZ" : "CQ";
                return 0;
            }
            if (n28 <= 1002)
            {
                result = "CQ " + IntToDd((int)n28 - 3, 3, false);
                return 0;
            }
            if (n28 <= 532443)
            {
                uint n = n28 - 1003;
                var aaaa = new char[4];
                for (int i = 3; ; --i)
                {
                    aaaa[i] = Charn((int)(n % 27), FtxCharTable.LettersSpace);
                    if (i == 0) break;
                    n /= 27;
                }
                result = "CQ " + new string(aaaa).TrimStart(' ');
                return 0;
            }
            return -1;
        }

        n28 -= NTokens;
        if (n28 < Max22)
        {
            result = LookupCallsign(hash, FtxHashType.Bits22, n28);
            return 0;
        }

        uint v = n28 - Max22;
        var cs = new char[6];
        cs[5] = Charn((int)(v % 27), FtxCharTable.LettersSpace); v /= 27;
        cs[4] = Charn((int)(v % 27), FtxCharTable.LettersSpace); v /= 27;
        cs[3] = Charn((int)(v % 27), FtxCharTable.LettersSpace); v /= 27;
        cs[2] = Charn((int)(v % 10), FtxCharTable.Numeric); v /= 10;
        cs[1] = Charn((int)(v % 36), FtxCharTable.Alphanum); v /= 36;
        cs[0] = Charn((int)(v % 37), FtxCharTable.AlphanumSpace);
        string callsign = new(cs);

        if (callsign.StartsWith("3D0", StringComparison.Ordinal) && !IsSpace(callsign[3]))
            result = "3DA0" + TrimSpaces(callsign[3..]);          // 3D0XYZ -> 3DA0XYZ
        else if (callsign[0] == 'Q' && IsLetter(callsign[1]))
            result = "3X" + TrimSpaces(callsign[1..]);            // QA0XYZ -> 3XA0XYZ
        else
            result = TrimSpaces(callsign);

        if (result.Length < 3) return -1;

        if (ip != 0)
        {
            if (i3 == 1) result += "/R";
            else if (i3 == 2) result += "/P";
            else return -2;
        }

        SaveCallsign(hash, result, out _);
        return 0;
    }

    /// <summary>pack58(): a non-standard call (up to 11 characters, a leading
    /// '&lt;' skipped, stopping at a '&lt;') in base 38.</summary>
    private static bool Pack58(IFtxCallsignHash? hash, string callsign, out ulong n58)
    {
        n58 = 0;
        int src = At(callsign, 0) == '<' ? 1 : 0;
        int length = 0;
        ulong result = 0;
        var c11 = new StringBuilder();
        while (At(callsign, src) != '\0' && At(callsign, src) != '<' && length < 11)
        {
            char c = callsign[src];
            c11.Append(c);
            int j = Nchar(c, FtxCharTable.AlphanumSpaceSlash);
            if (j < 0) return false;
            result = result * 38 + (ulong)j;
            src++;
            length++;
        }

        if (!SaveCallsign(hash, c11.ToString(), out _)) return false;
        n58 = result;
        return true;
    }

    private static string Unpack58(ulong n58, IFtxCallsignHash? hash)
    {
        var c11 = new char[11];
        for (int i = 10; ; --i)
        {
            c11[i] = Charn((int)(n58 % 38), FtxCharTable.AlphanumSpaceSlash);
            if (i == 0) break;
            n58 /= 38;
        }
        string callsign = TrimSpaces(new string(c11));
        if (callsign.Length >= 3) SaveCallsign(hash, callsign, out _);
        return callsign;
    }

    /// <summary>packgrid(): grid, report or token as the 16-bit field (bit 15 = R).
    /// Anything it does not recognise packs as a report of +00, as in the C.</summary>
    internal static ushort PackGrid(string grid4)
    {
        if (grid4.Length == 0) return MaxGrid4 + 1;               // two calls only
        if (grid4 == "RRR") return MaxGrid4 + 2;
        if (grid4 == "RR73") return MaxGrid4 + 3;
        if (grid4 == "73") return MaxGrid4 + 4;

        if (InRange(At(grid4, 0), 'A', 'R') && InRange(At(grid4, 1), 'A', 'R')
            && IsDigit(At(grid4, 2)) && IsDigit(At(grid4, 3)))
        {
            int igrid4 = grid4[0] - 'A';
            igrid4 = igrid4 * 18 + (grid4[1] - 'A');
            igrid4 = igrid4 * 10 + (grid4[2] - '0');
            igrid4 = igrid4 * 10 + (grid4[3] - '0');
            return (ushort)igrid4;
        }

        // Report: +dd / -dd / R+dd / R-dd — uint16 arithmetic, as the C.
        if (grid4[0] == 'R')
        {
            ushort irpt = (ushort)(35 + DdToInt(grid4[1..], 3));
            return (ushort)((ushort)(MaxGrid4 + irpt) | 0x8000);
        }
        else
        {
            ushort irpt = (ushort)(35 + DdToInt(grid4, 3));
            return (ushort)(MaxGrid4 + irpt);
        }
    }

    private static string UnpackGrid(ushort igrid4, byte ir)
    {
        if (igrid4 <= MaxGrid4)
        {
            int n = igrid4;
            var g = new char[4];
            g[3] = (char)('0' + n % 10); n /= 10;
            g[2] = (char)('0' + n % 10); n /= 10;
            g[1] = (char)('A' + n % 18); n /= 18;
            g[0] = (char)('A' + n % 18);
            return (ir > 0 ? "R " : "") + new string(g);
        }

        int irpt = igrid4 - MaxGrid4;
        return irpt switch
        {
            1 => "",
            2 => "RRR",
            3 => "RR73",
            4 => "73",
            _ => (ir > 0 ? "R" : "") + IntToDd(irpt - 35, 2, true),
        };
    }
}
