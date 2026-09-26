// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// The callsign hash table, ported from native/ft8/zeus_ft8.c. It must outlive
// a single decode: a non-standard call is spelled out once (e.g. "CQ II7ABB")
// and later messages refer to it only by hash, in LATER slots. It is a
// session-long cache, as in WSJT-X, reached from the decode worker and the
// keyer thread, hence the lock.
//
// Open addressing over 256 slots. A call's home slot is the top 10 bits of its
// 22-bit hash — the one part the 10-, 12- and 22-bit forms share (n12 = n22 >>
// 10, n10 = n22 >> 12) — so every lookup width probes from the same place.

namespace Zeus.Server.Hosting.Digital.Ft8;

public sealed class FtxCallsignTable : IFtxCallsignHash
{
    private const int Size = 256;

    private readonly string?[] _call = new string?[Size];
    private readonly uint[] _hash = new uint[Size];
    private readonly object _lock = new();

    private static int Home(uint hash22) => (int)(((hash22 >> 12) & 0x3FF) % Size);

    public void Save(string callsign, uint hash)
    {
        if (string.IsNullOrEmpty(callsign)) return;
        string call = callsign.Length > 11 ? callsign[..11] : callsign;

        lock (_lock)
        {
            int home = Home(hash);
            int idx = home;
            for (int i = 0; i < Size; i++)
            {
                if (_call[idx] == null)
                {
                    _call[idx] = call;
                    _hash[idx] = hash;
                    return;
                }
                // Already known. The C compares the stored (≤11-char) copy with
                // the full call, so a longer call never matches; kept as the C.
                if (_hash[idx] == hash && _call[idx] == callsign) return;
                idx = (idx + 1) % Size;
            }

            // Full: take the home slot rather than stop learning.
            _call[home] = call;
            _hash[home] = hash;
        }
    }

    public bool Lookup(FtxHashType type, uint hash, out string callsign)
    {
        int shift = type == FtxHashType.Bits10 ? 12 : type == FtxHashType.Bits12 ? 10 : 0;
        // Re-align the received hash to the top 10 bits used for the home slot.
        uint hash10 = (hash >> (12 - shift)) & 0x3FF;

        lock (_lock)
        {
            int idx = (int)(hash10 % Size);
            for (int i = 0; i < Size; i++)
            {
                // An empty slot ends the probe — tested first, or a received
                // hash of 0 would "match" an empty entry.
                if (_call[idx] == null) break;
                if (((_hash[idx] & 0x3FFFFF) >> shift) == hash)
                {
                    callsign = _call[idx]!;
                    return true;
                }
                idx = (idx + 1) % Size;
            }
        }
        callsign = "";
        return false;
    }
}
