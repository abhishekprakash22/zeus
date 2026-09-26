// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// WSPR Fano decoder, ported from wsprd's fano.c (Phil Karn KA9Q's soft-
// decision sequential decoder for the K=32 r=1/2 Layland-Lushbaugh code,
// minor modifications by K1JT). Integer arithmetic throughout, so the port
// decodes exactly what the C does from the same soft symbols.
//
// The branch-metric table is wsprd's: metric_tables[2] (2-FSK, Es/No 6 dB,
// simulated by K9AN) biased by 0.45 and scaled by 10 — the only row wsprd
// uses.

namespace Zeus.Server.Hosting.Digital.Wspr;

internal static class WsprFano
{
    /// <summary>metric_tables[2] from wsprd's metric_tables.h, verbatim.</summary>
    private static ReadOnlySpan<float> MetricRow =>
    [
        0.9999f, 0.9998f, 0.9998f, 0.9998f, 0.9998f, 0.9998f, 0.9997f, 0.9997f,
        0.9997f, 0.9997f, 0.9997f, 0.9996f, 0.9996f, 0.9996f, 0.9995f, 0.9995f,
        0.9994f, 0.9994f, 0.9994f, 0.9993f, 0.9993f, 0.9992f, 0.9991f, 0.9991f,
        0.9990f, 0.9989f, 0.9988f, 0.9988f, 0.9988f, 0.9986f, 0.9985f, 0.9984f,
        0.9983f, 0.9982f, 0.9980f, 0.9979f, 0.9977f, 0.9976f, 0.9974f, 0.9971f,
        0.9969f, 0.9968f, 0.9965f, 0.9962f, 0.9960f, 0.9957f, 0.9953f, 0.9950f,
        0.9947f, 0.9941f, 0.9937f, 0.9933f, 0.9928f, 0.9922f, 0.9917f, 0.9911f,
        0.9904f, 0.9897f, 0.9890f, 0.9882f, 0.9874f, 0.9863f, 0.9855f, 0.9843f,
        0.9832f, 0.9819f, 0.9806f, 0.9792f, 0.9777f, 0.9760f, 0.9743f, 0.9724f,
        0.9704f, 0.9683f, 0.9659f, 0.9634f, 0.9609f, 0.9581f, 0.9550f, 0.9516f,
        0.9481f, 0.9446f, 0.9406f, 0.9363f, 0.9317f, 0.9270f, 0.9218f, 0.9160f,
        0.9103f, 0.9038f, 0.8972f, 0.8898f, 0.8822f, 0.8739f, 0.8647f, 0.8554f,
        0.8457f, 0.8357f, 0.8231f, 0.8115f, 0.7984f, 0.7854f, 0.7704f, 0.7556f,
        0.7391f, 0.7210f, 0.7038f, 0.6840f, 0.6633f, 0.6408f, 0.6174f, 0.5939f,
        0.5678f, 0.5410f, 0.5137f, 0.4836f, 0.4524f, 0.4193f, 0.3850f, 0.3482f,
        0.3132f, 0.2733f, 0.2315f, 0.1891f, 0.1435f, 0.0980f, 0.0493f, 0.0000f,
        -0.0510f, -0.1052f, -0.1593f, -0.2177f, -0.2759f, -0.3374f, -0.4005f, -0.4599f,
        -0.5266f, -0.5935f, -0.6626f, -0.7328f, -0.8051f, -0.8757f, -0.9498f, -1.0271f,
        -1.1019f, -1.1816f, -1.2642f, -1.3459f, -1.4295f, -1.5077f, -1.5958f, -1.6818f,
        -1.7647f, -1.8548f, -1.9387f, -2.0295f, -2.1152f, -2.2154f, -2.3011f, -2.3904f,
        -2.4820f, -2.5786f, -2.6730f, -2.7652f, -2.8616f, -2.9546f, -3.0526f, -3.1445f,
        -3.2445f, -3.3416f, -3.4357f, -3.5325f, -3.6324f, -3.7313f, -3.8225f, -3.9209f,
        -4.0248f, -4.1278f, -4.2261f, -4.3193f, -4.4220f, -4.5262f, -4.6214f, -4.7242f,
        -4.8234f, -4.9245f, -5.0298f, -5.1250f, -5.2232f, -5.3267f, -5.4332f, -5.5342f,
        -5.6431f, -5.7270f, -5.8401f, -5.9350f, -6.0407f, -6.1418f, -6.2363f, -6.3384f,
        -6.4536f, -6.5429f, -6.6582f, -6.7433f, -6.8438f, -6.9478f, -7.0789f, -7.1894f,
        -7.2714f, -7.3815f, -7.4810f, -7.5575f, -7.6852f, -7.8071f, -7.8580f, -7.9724f,
        -8.1000f, -8.2207f, -8.2867f, -8.4017f, -8.5287f, -8.6347f, -8.7082f, -8.8319f,
        -8.9448f, -9.0355f, -9.1885f, -9.2095f, -9.2863f, -9.4186f, -9.5064f, -9.6386f,
        -9.7207f, -9.8286f, -9.9453f, -10.0701f, -10.1735f, -10.3001f, -10.2858f, -10.5427f,
        -10.5982f, -10.7361f, -10.7042f, -10.9212f, -11.0097f, -11.0469f, -11.1155f, -11.2812f,
        -11.3472f, -11.4988f, -11.5327f, -11.6692f, -11.9376f, -11.8606f, -12.1372f, -13.2539f,
    ];

    /// <summary>mettab[sent bit][received soft symbol], as wspr_decode builds it.</summary>
    internal static readonly int[,] MetTab = BuildMetTab();

    private static int[,] BuildMetTab()
    {
        const float bias = 0.45f;
        var t = new int[2, 256];
        var row = MetricRow;
        for (int i = 0; i < 256; i++)
        {
            // roundf(10.0 * (float - float)): double product, rounded as float, half away from 0.
            t[0, i] = (int)MathF.Round((float)(10.0 * (row[i] - bias)), MidpointRounding.AwayFromZero);
            t[1, i] = (int)MathF.Round((float)(10.0 * (row[255 - i] - bias)), MidpointRounding.AwayFromZero);
        }
        return t;
    }

    private struct Node
    {
        public uint EncState;   // encoder state of next node (low 32 bits are all ENCODE sees)
        public long Gamma;      // cumulative metric to this node
        public int M0, M1, M2, M3;
        public int Tm0, Tm1;    // sorted metrics for current hypotheses
        public int I;           // current branch being tested

        public readonly int Metric(int sym) => sym switch { 0 => M0, 1 => M1, 2 => M2, _ => M3 };
        public readonly int Tm(int i) => i == 0 ? Tm0 : Tm1;
    }

    private static int Encode(uint encstate) =>
        (WsprEncoder.Parity(encstate & WsprEncoder.Poly1) << 1) | WsprEncoder.Parity(encstate & WsprEncoder.Poly2);

    /// <summary>
    /// fano(): decode <paramref name="nbits"/> bits from deinterleaved soft
    /// symbols (0..255, 2 per bit). Writes nbits/8 bytes to
    /// <paramref name="data"/>. True on success; false on timeout
    /// (<paramref name="maxCycles"/> per bit), exactly as the C's return -1.
    /// </summary>
    public static bool Decode(ReadOnlySpan<byte> symbols, int nbits, int delta, int maxCycles,
                              Span<byte> data, out uint metric, out uint cycles)
    {
        var nodes = new Node[nbits + 1];
        int lastnode = nbits - 1, tail = nbits - 31;

        // All branch metrics for each symbol pair — the only look at the input.
        for (int k = 0; k <= lastnode; k++)
        {
            int s0 = symbols[2 * k], s1 = symbols[2 * k + 1];
            nodes[k].M0 = MetTab[0, s0] + MetTab[0, s1];
            nodes[k].M1 = MetTab[0, s0] + MetTab[1, s1];
            nodes[k].M2 = MetTab[1, s0] + MetTab[0, s1];
            nodes[k].M3 = MetTab[1, s0] + MetTab[1, s1];
        }

        int np = 0;
        nodes[np].EncState = 0;
        int lsym = Encode(nodes[np].EncState);      // 0-branch (LSB 0)
        int m0 = nodes[np].Metric(lsym);
        int m1 = nodes[np].Metric(3 ^ lsym);        // polynomials both odd: complementary pair
        if (m0 > m1) { nodes[np].Tm0 = m0; nodes[np].Tm1 = m1; }
        else { nodes[np].Tm0 = m1; nodes[np].Tm1 = m0; nodes[np].EncState++; }
        nodes[np].I = 0;
        uint maxcycles = (uint)maxCycles * (uint)nbits;
        nodes[np].Gamma = 0;
        int t = 0;

        uint i;
        for (i = 1; i <= maxcycles; i++)
        {
            // Look forward.
            int ngamma = (int)(nodes[np].Gamma + nodes[np].Tm(nodes[np].I));
            if (ngamma >= t)
            {
                if (nodes[np].Gamma < t + delta)
                    while (ngamma >= t + delta) t += delta;     // first visit: tighten
                nodes[np + 1].Gamma = ngamma;
                nodes[np + 1].EncState = nodes[np].EncState << 1;
                if (++np == lastnode + 1) break;                 // done

                lsym = Encode(nodes[np].EncState);
                if (np >= tail)
                {
                    nodes[np].Tm0 = nodes[np].Metric(lsym);     // tail is all zeroes
                }
                else
                {
                    m0 = nodes[np].Metric(lsym);
                    m1 = nodes[np].Metric(3 ^ lsym);
                    if (m0 > m1) { nodes[np].Tm0 = m0; nodes[np].Tm1 = m1; }
                    else { nodes[np].Tm0 = m1; nodes[np].Tm1 = m0; nodes[np].EncState++; }
                }
                nodes[np].I = 0;
                continue;
            }

            // Threshold violated, can't go forward: look backward.
            for (;;)
            {
                if (np == 0 || nodes[np - 1].Gamma < t)
                {
                    // Can't back up either: relax, look forward to the better branch.
                    t -= delta;
                    if (nodes[np].I != 0) { nodes[np].I = 0; nodes[np].EncState ^= 1; }
                    break;
                }
                if (--np < tail && nodes[np].I != 1)
                {
                    nodes[np].I++;                               // next best branch
                    nodes[np].EncState ^= 1;
                    break;
                }
            }
        }
        metric = (uint)nodes[np].Gamma;

        int nbytes = nbits >> 3;
        for (int b = 0, k = 7; b < nbytes; b++, k += 8) data[b] = (byte)nodes[k].EncState;
        cycles = i + 1;
        return i < maxcycles;                                   // the C: i >= maxcycles is a timeout
    }
}
