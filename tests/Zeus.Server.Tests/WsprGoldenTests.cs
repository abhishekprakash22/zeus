// SPDX-License-Identifier: GPL-2.0-or-later
//
// Golden tests: the managed WSPR decoder against the native wsprd oracle on
// the same audio. Same messages and drift; SNR, dt and frequency within
// small tolerances (libm vs .NET math and FFTW vs a managed FFT differ in
// the last bits). Skips where the native library is not staged.

using System.Diagnostics;
using Xunit.Abstractions;
using Zeus.Server.Hosting.Digital;
using Zeus.Server.Hosting.Digital.Wspr;

namespace Zeus.Server.Tests;

public sealed unsafe class WsprGoldenTests(ITestOutputHelper output)
{
    private const int Rate = 12_000, SpSym = 8_192;

    private sealed record Beacon(string Message, double OffsetHz, double Amp, double DriftHz = 0, double StartS = 1.0);

    /// <summary>A 120 s slot at 12 kHz: beacons (4-FSK, 1.4648 Hz spacing,
    /// linear drift ±drift/2 about the centre, as wsprd models it) in
    /// Gaussian noise of the given rms.</summary>
    private static float[] Slot(IEnumerable<Beacon> beacons, double noiseRms, int seed)
    {
        var audio = new float[120 * Rate];
        foreach (var b in beacons)
        {
            var sym = new byte[162];
            Assert.True(WsprEncoder.TryEncode(b.Message, sym));
            double phase = 0, spacing = 12_000.0 / 8_192.0;
            int start = (int)(b.StartS * Rate);
            for (int s = 0; s < 162; s++)
            {
                double drift = b.DriftHz / 2.0 * (s - 81) / 81.0;
                double dp = 2 * Math.PI * (1500.0 + b.OffsetHz + drift + (sym[s] - 1.5) * spacing) / Rate;
                for (int i = 0; i < SpSym; i++)
                {
                    phase += dp;
                    int k = start + s * SpSym + i;
                    if (k >= 0 && k < audio.Length) audio[k] += (float)(b.Amp * Math.Sin(phase));
                }
            }
        }
        var rng = new Random(seed);
        for (int i = 0; i < audio.Length; i++)
        {
            double u1 = 1 - rng.NextDouble(), u2 = rng.NextDouble();
            audio[i] += (float)(noiseRms * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }
        return audio;
    }

    private static List<WsprDecode> Native(float[] I, float[] Q, int dialHz)
    {
        var spots = new ZeusWsprSpot[64];
        int n;
        fixed (float* pi = I) fixed (float* pq = Q) fixed (ZeusWsprSpot* ps = spots)
            n = WsprNative.Decode(pi, pq, I.Length, dialHz, ps, spots.Length);
        var list = new List<WsprDecode>();
        for (int k = 0; k < n; k++)
        {
            string msg;
            fixed (byte* pm = spots[k].Message)
                msg = System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)pm) ?? "";
            list.Add(new WsprDecode(spots[k].FreqHz, spots[k].SnrDb, spots[k].DtSec, spots[k].DriftHz, msg, 0, 0, 0));
        }
        return list;
    }

    public static TheoryData<string> Cases => new() { "five-beacons", "types-1-2-3", "drifting", "weak", "crowded", "noise-only" };

    private static (Beacon[] Beacons, double Noise) Case(string name) => name switch
    {
        "five-beacons" => ([
            new("K1ABC FN42 30", -80, 0.05), new("EA5IUE IM76 23", -30, 0.10),
            new("G4XYZ IO91 37", 10, 0.15), new("DL1AB JO62 33", 55, 0.20), new("VK2ZZ QF56 40", 95, 0.25)], 0.5),
        "types-1-2-3" => ([
            new("K1ABC FN42 37", -60, 0.2), new("PJ4/K1ABC 37", -10, 0.2),
            new("EA5IUE/P 23", 30, 0.2), new("<K1ABC> FN42AB 37", 70, 0.2)], 0.5),
        "drifting" => ([
            new("W1AW FN31 33", -70, 0.2, DriftHz: 2), new("JA1XYZ PM95 40", 0, 0.2, DriftHz: -3),
            new("ZL2ABC RF70 30", 60, 0.2, DriftHz: 4)], 0.5),
        "weak" => ([
            new("K1ABC FN42 30", -40, 0.02), new("EA5IUE IM76 23", 20, 0.015), new("G4XYZ IO91 37", 80, 0.012)], 0.5),
        "crowded" => (Enumerable.Range(0, 12).Select(i =>
            new Beacon($"K{i % 10}AB{(char)('A' + i)} FN{40 + i % 10} {(i % 3) * 10 + 20}", -105 + i * 18, 0.03 + 0.01 * (i % 4))).ToArray(), 0.5),
        "noise-only" => ([], 0.5),
        _ => throw new ArgumentException(name),
    };

    [SkippableTheory]
    [MemberData(nameof(Cases))]
    public void Managed_DecodesWhatNativeDecodes(string name)
    {
        Skip.IfNot(WsprNative.Available, "native wsprd not staged for this platform");
        var (beacons, noise) = Case(name);
        var (I, Q) = WsprService.MixAndDecimate32(Slot(beacons, noise, seed: name.Length));
        const int dial = 14_095_600;

        var sw = Stopwatch.StartNew();
        var native = Native(I, Q, dial);
        long tn = sw.ElapsedMilliseconds;
        sw.Restart();
        var managed = WsprDecoder.Decode(I, Q, dial);
        long tm = sw.ElapsedMilliseconds;

        output.WriteLine($"{name}: native {native.Count} spots in {tn} ms, managed {managed.Count} in {tm} ms");
        foreach (var d in native) output.WriteLine($"  N {d.Message,-20} snr {d.SnrDb,6:0.0} dt {d.DtSec,5:0.00} f {d.FreqMhz:0.000000} drift {d.DriftHz}");
        foreach (var d in managed) output.WriteLine($"  M {d.Message,-20} snr {d.SnrDb,6:0.0} dt {d.DtSec,5:0.00} f {d.FreqMhz:0.000000} drift {d.DriftHz}");

        Assert.Equal(native.Select(d => d.Message).Order(), managed.Select(d => d.Message).Order());
        foreach (var n in native)
        {
            var m = managed.First(x => x.Message == n.Message);
            Assert.Equal(n.DriftHz, m.DriftHz);
            Assert.InRange(m.SnrDb - n.SnrDb, -0.5, 0.5);
            Assert.InRange(m.DtSec - n.DtSec, -0.03, 0.03);
            Assert.InRange((m.FreqMhz - n.FreqMhz) * 1e6, -0.3, 0.3);
        }
    }
}
