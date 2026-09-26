// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// WSPR decoder, ported from wsprd's wsprd.c (wspr_decode,
// sync_and_demodulate, subtract_signal2). Input: one 120 s slot of complex
// baseband at 375 Hz, the classic 1500 Hz WSPR window centred on 0 Hz — the
// same contract the native zeus_wspr_decode had; WsprService feeds it from
// MixAndDecimate32.
//
// FIDELITY. The port was built to decode exactly what the native library
// decoded, and was checked spot for spot against it (synthesized and real
// 20 m / 17 m slots) before native/wspr was retired; the golden tests hold that
// native output frozen in TestData/wspr. It therefore still reproduces the
// C's quirks, each marked "(as the C)" — fixing them is zeus-88xj.5, each
// with a test showing the gain:
//   * the coarse drift search divides by an unparenthesised macro
//     (`... * idrift / DF` with DF = 375.0 / 256.0 → `/ 375.0 / 256.0`), so
//     drift barely moves the bin there;
//   * the coarse search indexes the power matrix flat, so a negative time
//     index reads the tail of the previous frequency row;
//   * decodes the unpacker marks "noprint" are still reported;
//   * a message the encoder refuses (e.g. an empty ntype-63 decode) or an
//     "A000AA" grid ends the candidate loop for that pass.
// Floating-point work is done in float where the C uses float, double where
// it promotes to double. Differences left: libm vs .NET sin/cos/log ULPs and
// the FFT (managed radix-2 vs FFTW) — they can flip only borderline decodes.
//
// What the port does NOT copy: fftw_wisdom.dat reads/writes in the working
// directory, the static fplast cache (made local — the function is
// reentrant here), and the process-wide hash table (per call, as the shim
// used it: usehashtable = 0 allocates a fresh one every slot).
//
// Originals: Copyright 2001-2015 Joe Taylor K1JT; 2014-2015 Steven Franke
// K9AN; 2016 Guenael Jouchet VA2GKA. GPL.

namespace Zeus.Server.Hosting.Digital.Wspr;

/// <summary>One decoded spot. <see cref="FreqMhz"/> is absolute (dial + 1500 Hz + offset).</summary>
public sealed record WsprDecode(
    double FreqMhz, float SnrDb, float DtSec, float DriftHz, string Message,
    float Sync, uint Cycles, int Jitter);

public static class WsprDecoder
{
    public const int SampleRate = 375;
    public const int SlotSamples = 120 * SampleRate;        // 45000

    private const int NBits = 81, NSym = 162, NsPerSym = 256;
    private const int FftSize = 512;
    private const int MaxCandidates = 200, MaxUniques = 100;
    private const double DF = 375.0 / 256.0;                 // used parenthesised
    private const double TwoPiDt = 2.0 * Math.PI * 1.0 / 375.0;
    private const double Df05 = 375.0 / 256.0 * 0.5, Df15 = 375.0 / 256.0 * 1.5;

    /// <summary>
    /// Decode one slot. <paramref name="idat"/>/<paramref name="qdat"/> are
    /// copied (subtraction mutates them). Two passes with signal subtraction,
    /// as the Zeus shim ran the native decoder.
    /// </summary>
    public static List<WsprDecode> Decode(ReadOnlySpan<float> idat, ReadOnlySpan<float> qdat, int dialFreqHz,
                                          int npasses = 2, bool subtraction = true, bool quickmode = false)
    {
        int samples = Math.Min(idat.Length, qdat.Length);
        var id = idat[..samples].ToArray();
        var qd = qdat[..samples].ToArray();
        var decodes = new List<WsprDecode>();
        var hashtab = new WsprHashTable();

        // Performance-tuning parameters (wsprd).
        float minsync1 = 0.10f, minsync2 = 0.12f;
        const int iifac = 3, symfac = 50, delta = 60, maxcycles = 10000;
        int maxdrift = 4;
        float minrms = 52.0f * (symfac / 64.0f);
        const float fmin = -110.0f, fmax = 110.0f;

        int blocks = 4 * (int)Math.Floor(samples / (double)FftSize) - 1;
        if (blocks <= 0) return decodes;

        // Hann window (as the C: sinf(0.006147931 * i)).
        var hann = new float[FftSize];
        for (int i = 0; i < FftSize; i++) hann[i] = MathF.Sin((float)(0.006147931 * i));

        var fft = new Fft512();
        var re = new float[FftSize];
        var im = new float[FftSize];
        // ps[freq][time], flat as the C's float (*ps)[blocks].
        var ps = new float[FftSize * blocks];
        var psq = new float[FftSize * blocks];              // sqrtf(ps), for the coarse search

        var candidates = new Cand[MaxCandidates];
        var symbols = new byte[NBits * 2];
        var decdata = new byte[(NBits + 7) / 8 + 1];         // 11 bytes: [10] stays 0
        var allfreqs = new float[MaxUniques];
        var allcalls = new string[MaxUniques];
        int uniques = 0;

        for (int ipass = 0; ipass < npasses; ipass++)
        {
            if (ipass == 1 && uniques == 0) break;
            if (ipass < 2) { maxdrift = 4; minsync2 = 0.12f; }
            if (ipass == 2) { maxdrift = 0; minsync2 = 0.10f; }

            // FFT over 2 symbols, stepped by half symbols.
            for (int i = 0; i < blocks; i++)
            {
                for (int j = 0; j < FftSize; j++)
                {
                    int k = i * 128 + j;
                    re[j] = id[k] * hann[j];
                    im[j] = qd[k] * hann[j];
                }
                fft.Forward(re, im);
                for (int j = 0; j < FftSize; j++)
                {
                    int k = j + FftSize / 2;
                    if (k > FftSize - 1) k -= FftSize;
                    float p = re[k] * re[k] + im[k] * im[k];
                    ps[j * blocks + i] = p;
                    psq[j * blocks + i] = MathF.Sqrt(p);
                }
            }

            // Average spectrum.
            var psavg = new float[FftSize];
            for (int i = 0; i < blocks; i++)
                for (int j = 0; j < FftSize; j++)
                    psavg[j] += ps[j * blocks + i];

            // Smooth with a 7-point window, limit to ±150 Hz.
            var smspec = new float[411];
            for (int i = 0; i < 411; i++)
            {
                float v = 0;
                for (int j = -3; j <= 3; j++) v += psavg[256 - 205 + i + j];
                smspec[i] = v;
            }

            // Noise level: the 30th percentile (123/411).
            var tmpsort = (float[])smspec.Clone();
            Array.Sort(tmpsort);
            float noiseLevel = tmpsort[122];

            float minSnr = MathF.Pow(10.0f, -8.0f / 10.0f);
            const float snrScalingFactor = 26.3f;
            for (int j = 0; j < 411; j++)
            {
                smspec[j] = smspec[j] / noiseLevel - 1.0f;
                if (smspec[j] < minSnr) smspec[j] = (float)(0.1 * minSnr);
            }

            // Local maxima.
            Array.Clear(candidates);
            int npk = 0;
            for (int j = 1; j < 410; j++)
            {
                if (smspec[j] > smspec[j - 1] && smspec[j] > smspec[j + 1] && npk < MaxCandidates)
                {
                    candidates[npk].Freq = (float)((j - 205) * (DF / 2.0));
                    candidates[npk].Snr = (float)(10.0 * MathF.Log10(smspec[j]) - snrScalingFactor);
                    npk++;
                }
            }

            // Keep [fmin, fmax], best SNR first.
            int kept = 0;
            for (int j = 0; j < npk; j++)
                if (candidates[j].Freq >= fmin && candidates[j].Freq <= fmax) candidates[kept++] = candidates[j];
            npk = kept;
            SortBySnrDescending(candidates.AsSpan(0, npk));

            // Coarse shift (DT), freq and drift.
            for (int j = 0; j < npk; j++)
            {
                float sync = 0.0f, syncMax = -1e30f;
                int if0 = (int)(candidates[j].Freq / (DF / 2.0) + NsPerSym);
                for (int ifr = if0 - 1; ifr <= if0 + 1; ifr++)
                for (int k0 = -10; k0 < 22; k0++)
                for (int idrift = -maxdrift; idrift <= maxdrift; idrift++)
                {
                    float ss = 0.0f, pow = 0.0f;
                    for (int k = 0; k < NSym; k++)
                    {
                        // (as the C) `... * ((float)idrift) / DF` with an unparenthesised
                        // DF macro: the drift term is divided by 375.0 then by 256.0.
                        int ifd = (int)(ifr + (double)(((float)k - NBits) / NBits * idrift) / 375.0 / 256.0);
                        int kindex = k0 + 2 * k;
                        if (kindex < blocks)
                        {
                            // (as the C) flat indexing: a negative kindex reads the previous row.
                            float p0 = psq[(ifd - 3) * blocks + kindex];
                            float p1 = psq[(ifd - 1) * blocks + kindex];
                            float p2 = psq[(ifd + 1) * blocks + kindex];
                            float p3 = psq[(ifd + 3) * blocks + kindex];
                            ss += (2 * WsprEncoder.Sync[k] - 1) * ((p1 + p3) - (p0 + p2));
                            pow += p0 + p1 + p2 + p3;
                            sync = ss / pow;
                        }
                    }
                    if (sync > syncMax)
                    {
                        syncMax = sync;
                        candidates[j].Shift = 128 * (k0 + 1);
                        candidates[j].Drift = idrift;
                        candidates[j].Freq = (float)((ifr - NsPerSym) * (DF / 2.0));
                        candidates[j].Sync = sync;
                    }
                }
            }

            // Refine each candidate and try to decode it.
            for (int j = 0; j < npk; j++)
            {
                float freq = candidates[j].Freq, drift = candidates[j].Drift, sync = candidates[j].Sync;
                int shift = candidates[j].Shift;

                // Best lag (mode 0), then best frequency (mode 1).
                int lagmin = shift - 128, lagmax = shift + 128, lagstep = quickmode ? 16 : 8;
                SyncAndDemodulate(id, qd, samples, symbols, ref freq, 0, 0, 0f, ref shift,
                    lagmin, lagmax, lagstep, drift, symfac, out sync, 0);
                SyncAndDemodulate(id, qd, samples, symbols, ref freq, -2, 2, 0.1f, ref shift,
                    lagmin, lagmax, lagstep, drift, symfac, out sync, 1);

                candidates[j].Freq = freq;
                candidates[j].Shift = shift;
                candidates[j].Drift = drift;
                candidates[j].Sync = sync;

                bool worthATry = sync > minsync1;
                int idt = 0, ii = 0;
                bool notDecoded = true;
                uint cycles = 0;
                while (worthATry && notDecoded && idt <= 128 / iifac)
                {
                    ii = (idt + 1) / 2;
                    if (idt % 2 == 1) ii = -ii;
                    ii = iifac * ii;
                    int jiggered = shift + ii;

                    // Soft-decision symbols at this lag (mode 2).
                    SyncAndDemodulate(id, qd, samples, symbols, ref freq, 0, 0, 0.1f, ref jiggered,
                        lagmin, lagmax, lagstep, drift, symfac, out sync, 2);
                    float sq = 0.0f;
                    for (int i = 0; i < NSym; i++)
                    {
                        float y = symbols[i] - 128.0f;
                        sq += y * y;
                    }
                    float rms = MathF.Sqrt(sq / NSym);

                    if (sync > minsync2 && rms > minrms)
                    {
                        WsprUnpack.Deinterleave(symbols);
                        notDecoded = !WsprFano.Decode(symbols, NBits, delta, maxcycles, decdata, out _, out cycles);
                    }
                    idt++;
                    if (quickmode) break;
                }

                if (!worthATry || notDecoded) continue;

                decdata[10] = 0;
                var msg = WsprUnpack.Unpack(decdata, hashtab);
                if (subtraction && ipass == 0 && !msg.NoPrint)
                {
                    var channel = new byte[NSym];
                    // (as the C) a message the encoder refuses ends this pass's candidate loop.
                    if (!WsprEncoder.TryEncode(msg.CallLocPow, channel)) break;
                    SubtractSignal2(id, qd, samples, freq, shift, drift, channel);
                }

                // (as the C) this pattern ends the candidate loop too.
                if (msg.Loc == "A000AA") break;

                bool dupe = false;
                for (int u = 0; u < uniques; u++)
                    if (msg.Callsign == allcalls[u] && MathF.Abs(freq - allfreqs[u]) < 3.0f) dupe = true;
                if (dupe || uniques >= MaxUniques) continue;

                allcalls[uniques] = msg.Callsign;
                allfreqs[uniques] = freq;
                uniques++;
                double dialMhz = dialFreqHz / 1e6;
                decodes.Add(new WsprDecode(
                    FreqMhz: dialMhz + (1500.0 + freq) / 1e6,
                    SnrDb: candidates[j].Snr,
                    DtSec: (float)(shift * 1.0 / 375.0 - 2.0),
                    DriftHz: drift,
                    Message: msg.CallLocPow,
                    Sync: candidates[j].Sync,
                    Cycles: cycles,
                    Jitter: ii));
            }
        }

        // Best SNR first (stable, where the C's qsort is not).
        decodes.Sort((a, b) => b.SnrDb.CompareTo(a.SnrDb));
        return decodes;
    }

    private struct Cand
    {
        public float Freq, Snr, Drift, Sync;
        public int Shift;
    }

    private static void SortBySnrDescending(Span<Cand> c)
    {
        // Stable insertion sort (≤ 200 entries).
        for (int i = 1; i < c.Length; i++)
        {
            var x = c[i];
            int j = i - 1;
            while (j >= 0 && c[j].Snr < x.Snr) { c[j + 1] = c[j]; j--; }
            c[j + 1] = x;
        }
    }

    /// <summary>
    /// sync_and_demodulate(). mode 0: best time lag (no freq/drift search);
    /// mode 1: best frequency at the given lag; mode 2: soft-decision symbols
    /// at the given lag and frequency.
    /// </summary>
    private static void SyncAndDemodulate(float[] id, float[] qd, int np, byte[] symbols,
        ref float freq, int ifmin, int ifmax, float fstep, ref int shift,
        int lagmin, int lagmax, int lagstep, float drift, int symfac, out float syncOut, int mode)
    {
        Span<float> c0 = stackalloc float[NsPerSym], s0 = stackalloc float[NsPerSym];
        Span<float> c1 = stackalloc float[NsPerSym], s1 = stackalloc float[NsPerSym];
        Span<float> c2 = stackalloc float[NsPerSym], s2 = stackalloc float[NsPerSym];
        Span<float> c3 = stackalloc float[NsPerSym], s3 = stackalloc float[NsPerSym];
        Span<float> fsymb = stackalloc float[NSym];

        float fbest = 0.0f;
        int bestShift = 0;
        float fplast = -10000.0f;                            // was a C static; local here
        float syncmax = -1e30f;

        if (mode == 0) { ifmin = 0; ifmax = 0; fstep = 0.0f; }
        else if (mode == 1) { lagmin = shift; lagmax = shift; }
        else { lagmin = shift; lagmax = shift; ifmin = 0; ifmax = 0; }

        for (int ifreq = ifmin; ifreq <= ifmax; ifreq++)
        {
            float f0 = freq + ifreq * fstep;
            for (int lag = lagmin; lag <= lagmax; lag += lagstep)
            {
                float ss = 0.0f, totp = 0.0f;
                for (int i = 0; i < NSym; i++)
                {
                    float fp = (float)(f0 + (drift / 2.0) * ((float)i - NBits) / NBits);
                    if (i == 0 || fp != fplast)
                    {
                        float dphi0 = (float)(TwoPiDt * (fp - Df15));
                        float dphi1 = (float)(TwoPiDt * (fp - Df05));
                        float dphi2 = (float)(TwoPiDt * (fp + Df05));
                        float dphi3 = (float)(TwoPiDt * (fp + Df15));
                        Oscillator(c0, s0, MathF.Cos(dphi0), MathF.Sin(dphi0));
                        Oscillator(c1, s1, MathF.Cos(dphi1), MathF.Sin(dphi1));
                        Oscillator(c2, s2, MathF.Cos(dphi2), MathF.Sin(dphi2));
                        Oscillator(c3, s3, MathF.Cos(dphi3), MathF.Sin(dphi3));
                        fplast = fp;
                    }

                    float i0 = 0, q0 = 0, i1 = 0, q1 = 0, i2 = 0, q2 = 0, i3 = 0, q3 = 0;
                    for (int j = 0; j < NsPerSym; j++)
                    {
                        int k = lag + i * NsPerSym + j;
                        if (k > 0 && k < np)                     // (as the C) k > 0, not >= 0
                        {
                            float x = id[k], y = qd[k];
                            i0 = i0 + x * c0[j] + y * s0[j];
                            q0 = q0 - x * s0[j] + y * c0[j];
                            i1 = i1 + x * c1[j] + y * s1[j];
                            q1 = q1 - x * s1[j] + y * c1[j];
                            i2 = i2 + x * c2[j] + y * s2[j];
                            q2 = q2 - x * s2[j] + y * c2[j];
                            i3 = i3 + x * c3[j] + y * s3[j];
                            q3 = q3 - x * s3[j] + y * c3[j];
                        }
                    }

                    float p0 = (float)Math.Sqrt(i0 * i0 + q0 * q0);
                    float p1 = (float)Math.Sqrt(i1 * i1 + q1 * q1);
                    float p2 = (float)Math.Sqrt(i2 * i2 + q2 * q2);
                    float p3 = (float)Math.Sqrt(i3 * i3 + q3 * q3);

                    totp = totp + p0 + p1 + p2 + p3;
                    float cmet = (p1 + p3) - (p0 + p2);
                    ss = WsprEncoder.Sync[i] == 1 ? ss + cmet : ss - cmet;
                    if (mode == 2)
                        fsymb[i] = WsprEncoder.Sync[i] == 1 ? p3 - p1 : p2 - p0;
                }
                ss = ss / totp;
                if (ss > syncmax)
                {
                    syncmax = ss;
                    bestShift = lag;
                    fbest = f0;
                }
            }
        }

        syncOut = syncmax;
        if (mode <= 1)
        {
            shift = bestShift;
            freq = fbest;
            return;
        }

        // Normalise the soft symbols.
        float fsum = 0.0f, f2sum = 0.0f;
        for (int i = 0; i < NSym; i++)
        {
            fsum += fsymb[i] / NSym;
            f2sum += fsymb[i] * fsymb[i] / NSym;
        }
        float fac = (float)Math.Sqrt(f2sum - fsum * fsum);
        for (int i = 0; i < NSym; i++)
        {
            float v = symfac * fsymb[i] / fac;
            if (v > 127) v = 127.0f;
            if (v < -128) v = -128.0f;
            symbols[i] = (byte)(int)(v + 128);
        }
    }

    private static void Oscillator(Span<float> c, Span<float> s, float cd, float sd)
    {
        c[0] = 1; s[0] = 0;
        for (int j = 1; j < NsPerSym; j++)
        {
            c[j] = c[j - 1] * cd - s[j - 1] * sd;
            s[j] = c[j - 1] * sd + s[j - 1] * cd;
        }
    }

    /// <summary>subtract_signal2(): subtract the coherent component of a
    /// decoded signal (reference × low-passed complex amplitude).</summary>
    private static void SubtractSignal2(float[] id, float[] qd, int np, float f0, int shift, float drift,
                                        ReadOnlySpan<byte> channelSymbols)
    {
        const int nfilt = 360;
        const int n = NSym * NsPerSym;
        var refi = new float[SlotSamples];
        var refq = new float[SlotSamples];
        var ci = new float[SlotSamples];
        var cq = new float[SlotSamples];
        var cfi = new float[SlotSamples];
        var cfq = new float[SlotSamples];

        // Reference WSPR signal centred on f0 (phase accumulated in float, as the C).
        float phi = 0.0f;
        for (int i = 0; i < NSym; i++)
        {
            float cs = channelSymbols[i];
            float dphi = (float)(TwoPiDt * (f0 + (drift / 2.0) * ((float)i - NSym / 2.0) / (NSym / 2.0)
                                            + (cs - 1.5) * 375.0 / 256.0));   // (as the C) DF macro
            for (int j = 0; j < NsPerSym; j++)
            {
                int ii = NsPerSym * i + j;
                refi[ii] = MathF.Cos(phi);
                refq[ii] = MathF.Sin(phi);
                phi = phi + dphi;
            }
        }

        // Low-pass window, normalised, with partial sums for the edges.
        var w = new float[nfilt];
        var partialsum = new float[nfilt];
        float norm = 0;
        for (int i = 0; i < nfilt; i++)
        {
            w[i] = MathF.Sin((float)(Math.PI * (float)i / (float)(nfilt - 1)));
            norm += w[i];
        }
        for (int i = 0; i < nfilt; i++) w[i] /= norm;
        for (int i = 1; i < nfilt; i++) partialsum[i] = partialsum[i - 1] + w[i];

        // s(t) · conj(r(t)), padded by nfilt at the front.
        for (int i = 0; i < n; i++)
        {
            int k = shift + i;
            if (k > 0 && k < np)
            {
                ci[i + nfilt] = id[k] * refi[i] + qd[k] * refq[i];
                cq[i + nfilt] = qd[k] * refi[i] - id[k] * refq[i];
            }
        }

        // LPF.
        for (int i = nfilt / 2; i < SlotSamples - nfilt / 2; i++)
        {
            float a = 0, b = 0;
            for (int j = 0; j < nfilt; j++)
            {
                a += w[j] * ci[i - nfilt / 2 + j];
                b += w[j] * cq[i - nfilt / 2 + j];
            }
            cfi[i] = a;
            cfq[i] = b;
        }

        // Subtract c(t)·r(t), correcting the LPF step response at both ends.
        for (int i = 0; i < n; i++)
        {
            if (i < nfilt / 2) norm = partialsum[nfilt / 2 + i];
            else if (i > n - 1 - nfilt / 2) norm = partialsum[nfilt / 2 + n - 1 - i];
            else norm = 1.0f;
            int k = shift + i, j = i + nfilt;
            if (k > 0 && k < np)
            {
                id[k] = id[k] - (cfi[j] * refi[i] - cfq[j] * refq[i]) / norm;
                qd[k] = qd[k] - (cfi[j] * refq[i] + cfq[j] * refi[i]) / norm;
            }
        }
    }

    /// <summary>512-point complex forward FFT (unnormalised, like FFTW_FORWARD).</summary>
    private sealed class Fft512
    {
        private readonly float[] _cos = new float[FftSize / 2];
        private readonly float[] _sin = new float[FftSize / 2];
        private readonly int[] _rev = new int[FftSize];

        public Fft512()
        {
            for (int i = 0; i < FftSize / 2; i++)
            {
                double a = -2 * Math.PI * i / FftSize;
                _cos[i] = (float)Math.Cos(a);
                _sin[i] = (float)Math.Sin(a);
            }
            int bits = 9;
            for (int i = 0; i < FftSize; i++)
            {
                int r = 0;
                for (int b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
                _rev[i] = r;
            }
        }

        public void Forward(float[] re, float[] im)
        {
            for (int i = 0; i < FftSize; i++)
            {
                int j = _rev[i];
                if (j > i) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
            }
            for (int size = 2; size <= FftSize; size <<= 1)
            {
                int half = size >> 1, step = FftSize / size;
                for (int start = 0; start < FftSize; start += size)
                    for (int k = 0; k < half; k++)
                    {
                        float wr = _cos[k * step], wi = _sin[k * step];
                        int a = start + k, b = a + half;
                        float tr = re[b] * wr - im[b] * wi;
                        float ti = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                    }
            }
        }
    }
}
