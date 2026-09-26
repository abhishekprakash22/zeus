// SPDX-License-Identifier: GPL-2.0-or-later
//
// Golden tests: the managed WSPR decoder against what the native wsprd
// decoded on the same audio, frozen into TestData/wspr before native/wspr
// was retired (synthetic-native.tsv for the synthesized slots, the "N" lines
// of each recorded slot's .txt). Since wsprd's own bugs were fixed
// (zeus-88xj.5) the managed decoder finds more than native did, so the rule
// is: every native spot is still found (SNR, dt and frequency within small
// tolerances), and every extra spot is real — a beacon that was actually
// synthesized, or, on a recorded slot, a spot confirmed on WSPRnet
// (wsprnet-verified.tsv). Runs on every platform.

using System.Diagnostics;
using Xunit.Abstractions;
using Zeus.Server.Hosting.Digital;
using Zeus.Server.Hosting.Digital.Wspr;

namespace Zeus.Server.Tests;

public sealed class WsprGoldenTests(ITestOutputHelper output)
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

    private static readonly string CorpusDir =
        Path.Combine(AppContext.BaseDirectory, "TestData", "wspr");

    /// <summary>The native decoder's spots for a synthesized case.</summary>
    private static List<WsprDecode> Frozen(string name)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return File.ReadAllLines(Path.Combine(CorpusDir, "synthetic-native.tsv"))
            .Where(l => !l.StartsWith('#') && l.Length > 0)
            .Select(l => l.Split('\t'))
            .Where(f => f[0] == name)
            .Select(f => new WsprDecode(double.Parse(f[4], inv), float.Parse(f[2], inv), float.Parse(f[3], inv),
                                        float.Parse(f[5], inv), f[1], 0, 0, 0))
            .ToList();
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Managed_DecodesWhatNativeDecoded(string name)
    {
        var (beacons, noise) = Case(name);
        var (I, Q) = WsprService.MixAndDecimate32(Slot(beacons, noise, seed: name.Length));
        const int dial = 14_095_600;

        var native = Frozen(name);
        var sw = Stopwatch.StartNew();
        var managed = WsprDecoder.Decode(I, Q, dial);
        output.WriteLine($"{name}: native {native.Count} spots, managed {managed.Count} in {sw.ElapsedMilliseconds} ms");
        foreach (var d in managed) output.WriteLine($"  M {d.Message,-20} snr {d.SnrDb,6:0.0} dt {d.DtSec,5:0.00} f {d.FreqMhz:0.000000} drift {d.DriftHz}");

        // Nothing native found is lost, and it is measured the same way.
        foreach (var n in native)
        {
            var m = managed.FirstOrDefault(x => x.Message == n.Message);
            Assert.True(m is not null, $"{name}: lost native spot {n.Message}");
            Assert.InRange(m!.SnrDb - n.SnrDb, -0.5, 0.5);
            Assert.InRange(m.DtSec - n.DtSec, -0.03, 0.03);
            Assert.InRange((m.FreqMhz - n.FreqMhz) * 1e6, -0.3, 0.3);
        }

        // Every spot is a beacon that was sent, with its drift found (wsprd's
        // coarse search divided the drift term by 375·256 and hardly saw it).
        foreach (var m in managed)
        {
            var b = beacons.FirstOrDefault(x => x.Message == m.Message);
            Assert.True(b is not null, $"{name}: false spot {m.Message}");
            Assert.InRange(m.DriftHz - b!.DriftHz, -1, 1);
        }
    }

    // wsprd decoded only 2 of these 3 drifting beacons; with the drift search
    // fixed all three decode, each with its own drift.
    [Fact]
    public void DriftingBeacons_AllDecode()
    {
        var (beacons, noise) = Case("drifting");
        var (I, Q) = WsprService.MixAndDecimate32(Slot(beacons, noise, seed: "drifting".Length));
        Assert.Equal(2, Frozen("drifting").Count);
        var managed = WsprDecoder.Decode(I, Q, 14_095_600);
        Assert.Equal(beacons.Select(b => b.Message).Order(), managed.Select(d => d.Message).Order());
    }

    // ---- recorded slots (ZEUS_WSPR_CAPTURE_DIR output) ---------------------

    public static TheoryData<string> Recorded()
    {
        var d = new TheoryData<string>();
        if (Directory.Exists(CorpusDir))
            foreach (var f in Directory.EnumerateFiles(CorpusDir, "*.iq").Order())
                d.Add(Path.GetFileName(f));
        if (d.Count == 0) d.Add("");                    // keeps the theory non-empty; skipped below
        return d;
    }

    /// <summary>Real on-air slots captured by WsprService: the managed decoder
    /// must find the spots native found on each (the .txt "N" lines), and any
    /// other spot must be one confirmed on WSPRnet.</summary>
    [SkippableTheory]
    [MemberData(nameof(Recorded))]
    public void Managed_DecodesWhatNativeDecoded_OnRecordedSlots(string file)
    {
        Skip.If(file.Length == 0, "no recorded slots in TestData/wspr yet");
        var bytes = File.ReadAllBytes(Path.Combine(CorpusDir, file));
        var all = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes).ToArray();
        int n = all.Length / 2;
        int dial = int.Parse(Path.GetFileNameWithoutExtension(file).Split('_')[1]);
        var native = File.ReadAllLines(Path.Combine(CorpusDir, Path.ChangeExtension(file, ".txt")))
            .Where(l => l.StartsWith("N ")).Select(l => l[2..]).Order().ToList();

        var verified = File.ReadAllLines(Path.Combine(CorpusDir, "wsprnet-verified.tsv"))
            .Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split('\t'))
            .Where(f => f[0] == file).Select(f => f[1]).ToList();

        var managed = WsprDecoder.Decode(all[..n], all[n..], dial).Select(d => d.Message).Order().ToList();
        output.WriteLine($"{file}: native [{string.Join(" | ", native)}]");
        output.WriteLine($"{file}: managed [{string.Join(" | ", managed)}]");
        Assert.Empty(native.Except(managed));                    // nothing lost
        Assert.Equal(verified.Order(), managed.Except(native));  // every gain confirmed on WSPRnet
    }
}
