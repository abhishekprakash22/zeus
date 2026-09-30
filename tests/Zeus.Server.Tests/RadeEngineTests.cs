// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Zeus.Server.Hosting.FreeDv;

namespace Zeus.Server.Tests;

/// <summary>
/// The managed (RadeSharp) and native (zeus_rade) RADE engines must be
/// interchangeable on air: same geometry, and each decodes the other's
/// modem signal including the End-of-Over callsign.
/// </summary>
public class RadeEngineTests
{
    private static short[] Buzz(int samples)
    {
        var s = new short[samples];
        for (int i = 0; i < samples; i++)
        {
            float t = i / 16000f, v = 0;
            for (int h = 1; h <= 8; h++) v += MathF.Sin(2 * MathF.PI * 140 * h * t) / h;
            s[i] = (short)(0.15f * v * 32767);
        }
        return s;
    }

    /// <summary>TX on one engine, real part only (as on air), RX on the other.</summary>
    private static (int pcm, bool synced, string call) OverTheAir(IRadeEngine tx, IRadeEngine rx)
    {
        tx.SetTxCallsign("EA5IUE");
        var speech = Buzz(4 * 16000);
        var air = new List<float>();
        var iq = new float[2 * Math.Max(tx.NTxOut, tx.NTxEooOut)];
        for (int i = 0; i + tx.NSpeechSamples <= speech.Length; i += tx.NSpeechSamples)
        {
            int n = tx.Tx(speech.AsSpan(i, tx.NSpeechSamples), iq);
            for (int k = 0; k < n; k++) { air.Add(iq[2 * k]); air.Add(0f); }
        }
        int ne = tx.TxEoo(iq);
        for (int k = 0; k < ne; k++) { air.Add(iq[2 * k]); air.Add(0f); }
        for (int k = 0; k < 2 * tx.NTxEooOut; k++) air.Add(0f);

        var samples = air.ToArray();
        var pcm = new short[rx.MaxPcmPerRx];
        var call = new byte[16];
        int pcmTotal = 0, pos = 0;
        bool synced = false;
        string got = "";
        while (pos + 2 * rx.Nin <= samples.Length)
        {
            int nin = rx.Nin;
            pcmTotal += rx.Rx(samples.AsSpan(pos, 2 * nin), pcm);
            pos += 2 * nin;
            synced |= rx.Sync;
            int n = rx.GetEooCallsign(call);
            if (n > 0) got = System.Text.Encoding.ASCII.GetString(call, 0, n);
        }
        return (pcmTotal, synced, got);
    }

    [Fact]
    public void ManagedEngine_Loopback()
    {
        using var tx = new ManagedRadeEngine();
        using var rx = new ManagedRadeEngine();
        var (pcm, synced, call) = OverTheAir(tx, rx);
        Assert.True(synced);
        Assert.True(pcm > 16000, $"only {pcm} PCM samples");
        Assert.Equal("EA5IUE", call);
    }

    [Fact]
    public void ManagedEngine_V2_Loopback()
    {
        using var tx = new ManagedRadeEngine(RadeSharp.RadeMode.V2);
        using var rx = new ManagedRadeEngine(RadeSharp.RadeMode.V2);
        Assert.Contains("V2", tx.Name);
        var (pcm, synced, call) = OverTheAir(tx, rx);
        Assert.True(synced);
        Assert.True(pcm > 16000, $"only {pcm} PCM samples");
        Assert.Equal("", call);   // RADE V2 has no EOO callsign channel
    }

    [SkippableFact]
    public void ManagedAndNativeEngines_Interoperate()
    {
        Skip.IfNot(RadeNative.Available, "libzeus_rade is not staged for this RID — managed/native interop NOT exercised.");
        using var managed = new ManagedRadeEngine();
        var z = RadeNative.Open();
        Assert.NotEqual(IntPtr.Zero, z);
        using var native = new NativeRadeEngine(z);

        Assert.Equal(native.NinMax, managed.NinMax);
        Assert.Equal(native.NSpeechSamples, managed.NSpeechSamples);
        Assert.Equal(native.NTxOut, managed.NTxOut);
        Assert.Equal(native.NTxEooOut, managed.NTxEooOut);

        var nToM = OverTheAir(native, managed);
        Assert.True(nToM.synced, "managed RX did not sync to native TX");
        Assert.True(nToM.pcm > 16000, $"managed RX decoded only {nToM.pcm} samples from native TX");
        Assert.Equal("EA5IUE", nToM.call);

        using var managed2 = new ManagedRadeEngine();
        var z2 = RadeNative.Open();
        using var native2 = new NativeRadeEngine(z2);
        var mToN = OverTheAir(managed2, native2);
        Assert.True(mToN.synced, "native RX did not sync to managed TX");
        Assert.True(mToN.pcm > 16000, $"native RX decoded only {mToN.pcm} samples from managed TX");
        Assert.Equal("EA5IUE", mToN.call);
    }
}
