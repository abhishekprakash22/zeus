// SPDX-License-Identifier: GPL-2.0-or-later
//
// FreeDV in-core modem tests. The resampler tests always run; the modem
// loopback runs only where libcodec2 is staged (runtimes/{rid}/native beside
// the test binary — true on the linux/macos CI legs and any dev box that has
// run native/build steps). Where the native is absent the modem must degrade
// exactly as the seam promises: NativeAvailable=false, Active=false, every
// IAudioModemPlugin member callable without throwing.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server.Hosting.FreeDv;

namespace Zeus.Server.Tests;

public sealed class FreeDvModemTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-freedv-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private static double Rms(ReadOnlySpan<float> x)
    {
        double s = 0;
        for (int i = 0; i < x.Length; i++) s += x[i] * x[i];
        return Math.Sqrt(s / Math.Max(1, x.Length));
    }

    [Fact]
    public void Resampler_RoundTrip_IsUnityGain_InThePassband()
    {
        var d = new Decimator48To8();
        var u = new Interpolator8To48();
        const int n = 48_000;
        var in48 = new float[n];
        for (int i = 0; i < n; i++)
            in48[i] = 0.5f * MathF.Sin(2 * MathF.PI * 1000 * i / 48_000f);

        var mid8 = new float[n / 6 + 8];
        int n8 = d.Process(in48, mid8);
        Assert.InRange(n8, n / 6 - 1, n / 6 + 1);

        var out48 = new float[n8 * 6];
        int n48 = u.Process(mid8.AsSpan(0, n8), out48);
        Assert.Equal(n8 * 6, n48);

        // Skip the FIR settle at both ends; a 1 kHz tone must come back at
        // unity within 5 %.
        double gIn = Rms(in48);
        double gOut = Rms(out48.AsSpan(200, n48 - 400));
        Assert.InRange(gOut / gIn, 0.95, 1.05);
    }

    [Fact]
    public void Resampler_Process_SurvivesArbitraryBlockSizes()
    {
        // The pipeline block size is not divisible by 6; the phase must
        // persist across calls so chunked == monolithic.
        var mono = new Decimator48To8();
        var chunked = new Decimator48To8();
        var rnd = new Random(42);
        var in48 = new float[10_000];
        for (int i = 0; i < in48.Length; i++) in48[i] = (float)(rnd.NextDouble() - 0.5);

        var outMono = new float[2000];
        int nMono = mono.Process(in48, outMono);

        var outChunk = new float[2000];
        int nChunk = 0, off = 0;
        foreach (int step in new[] { 7, 129, 1024, 333, 8507 })
        {
            int take = Math.Min(step, in48.Length - off);
            nChunk += chunked.Process(in48.AsSpan(off, take), outChunk.AsSpan(nChunk));
            off += take;
        }

        Assert.Equal(nMono, nChunk);
        for (int i = 0; i < nMono; i++)
            Assert.Equal(outMono[i], outChunk[i], 6);
    }

    [Fact]
    public async Task Modem_WithoutOrWithNative_HonoursTheSeamContract()
    {
        using var store = new FreeDvSettingsStore(
            NullLogger<FreeDvSettingsStore>.Instance, _dbPath);
        using var modem = new FreeDvModemService(
            NullLogger<FreeDvModemService>.Instance, store);
        await modem.StartAsync(default);

        // Every member must be callable regardless of native presence.
        var block = new float[1024];
        modem.SyncMode((byte)Zeus.Contracts.RxMode.FreeDv);
        modem.ProcessRx(block);
        modem.ProcessTx(block);
        modem.FlushRx();
        modem.FlushTx();
        _ = modem.FinishTx();
        _ = modem.DrainTx(block);
        modem.SyncMode(0);
        Assert.False(modem.Active);

        var st = modem.Snapshot();
        Assert.Equal(FreeDvNative.Available, st.NativeAvailable);
        Assert.False(st.RadeAvailable);
        Assert.Equal(8000, st.SpeechSampleRateHz);
        Assert.Equal(8000, st.ModemSampleRateHz);

        await modem.StopAsync(default);
    }

    [Fact]
    public async Task Modem_CleanLoopback_SyncsAndDecodes_700D()
    {
        if (!FreeDvNative.Available) return; // native not staged on this leg

        using var store = new FreeDvSettingsStore(
            NullLogger<FreeDvSettingsStore>.Instance, _dbPath);
        using var modem = new FreeDvModemService(
            NullLogger<FreeDvModemService>.Instance, store);
        await modem.StartAsync(default);
        modem.Configure(FreeDvSubmode.Mode700D, autoDetect: false,
            squelchEnabled: false, snrSquelchThreshDb: null, txText: "TEST");
        modem.SyncMode((byte)Zeus.Contracts.RxMode.FreeDv);
        Assert.True(modem.Active);

        // 4 s of tone "speech" TX → modem audio, plus the FinishTx tail.
        const int rate = 48_000;
        var onAir = new List<float>(5 * rate);
        var blk = new float[1024];
        for (int off = 0; off < 4 * rate; off += blk.Length)
        {
            for (int i = 0; i < blk.Length; i++)
                blk[i] = 0.25f * MathF.Sin(2 * MathF.PI * 400 * (off + i) / rate);
            modem.ProcessTx(blk);
            onAir.AddRange(blk);
        }
        int pending = modem.FinishTx();
        Assert.True(pending > 0);
        int real;
        while ((real = modem.DrainTx(blk)) > 0)
            onAir.AddRange(blk.Take(real));

        Assert.True(Rms(System.Runtime.InteropServices.CollectionsMarshal
            .AsSpan(onAir)) > 0.01);

        // Feed it straight back: clean channel must sync with decoded speech.
        modem.FlushRx();
        int speechBlocks = 0;
        var air = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(onAir);
        for (int off = 0; off + blk.Length <= air.Length; off += blk.Length)
        {
            air.Slice(off, blk.Length).CopyTo(blk);
            modem.ProcessRx(blk);
            if (Rms(blk) > 1e-4) speechBlocks++;
        }

        var st = modem.Snapshot();
        Assert.True(st.Synced, "no sync on a clean loopback");
        Assert.True(st.SnrDb > 5, $"SNR {st.SnrDb:F1} dB too low for clean loopback");
        Assert.True(speechBlocks > 20, "no decoded speech reached the output");

        await modem.StopAsync(default);
    }

    [Fact]
    public void SettingsStore_Persists_Modem_And_Reporter_Rows()
    {
        using (var store = new FreeDvSettingsStore(
            NullLogger<FreeDvSettingsStore>.Instance, _dbPath))
        {
            store.SetModem(new FreeDvModemSettings(
                FreeDvSubmode.Mode700E, true, false, 4.5, "DE TEST"));
            store.SetReporter(new FreeDvReporterSettings(true, "vu2xyz", "mk68", "hi"));
        }

        using var reopened = new FreeDvSettingsStore(
            NullLogger<FreeDvSettingsStore>.Instance, _dbPath);
        var m = reopened.GetModem();
        Assert.Equal(FreeDvSubmode.Mode700E, m.Submode);
        Assert.True(m.AutoDetect);
        Assert.False(m.SquelchEnabled);
        Assert.Equal(4.5, m.SnrSquelchThreshDb, 9);
        Assert.Equal("DE TEST", m.TxText);

        var r = reopened.GetReporter();
        Assert.True(r.ReportEnabled);
        Assert.Equal("VU2XYZ", r.Callsign);   // normalized upper-case
        Assert.Equal("MK68", r.GridSquare);
    }
}
