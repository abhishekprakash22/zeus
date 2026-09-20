// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// Ft8KeyerService — THE MODULATOR. The last leg of the FT8/FT4 TX chain:
//
//   frontend sequencer (ft8-sequencer.ts) → POST /ft8/tx stages a message
//   → TxStageBook holds it → THIS SERVICE watches the SlotClock, and at each
//   matching-parity boundary while ARMED it encodes the staged text
//   (ft8_lib pack/CRC/LDPC via zeus_ft8_synth), synthesizes the GFSK
//   waveform at the staged audio offset, keys MOX, streams the audio into the
//   normal TX mic path, and unkeys.
//
// Modelled directly on SignalJammerTxSource: same TxAudioIngest float32-LE
// block emission at the TxMicBlockResampler output rate, same stopwatch-paced
// real-time pump with pro-audio thread priority, same MOX
// ownership-token discipline (request with our own source, release only if we
// still own it, release in finally so a fault can never leave the PA keyed).
//
// SAFETY:
//  - Hard watchdog: a transmission may never exceed the waveform duration
//    + 3 s of pacing slack; past that the pump aborts and MOX drops.
//  - HALT / disarm (DigitalService.Armed=false) aborts mid-signal within one
//    audio block (~ms), then unkeys.
//  - MOX release runs in `finally` with a short guard delay (let the last
//    block drain) — mirroring the jammer's AutoTransmitReleaseGuard.
//  - The stage is one-shot: cleared after each keying (success OR failure).
//    The frontend runner re-stages every cycle while armed, so a stale
//    message can never repeat. TxStageBook.Eligible additionally enforces
//    parity + one-cycle freshness.
//
// TIMING: audio starts TxStartDelayMs (500 ms) after the slot boundary — the
// WSJT-X convention for the nominal FT8/FT4 signal start, so remote stations
// see DT ≈ 0 (plus path/pipeline latency). KeyLeadMs before the boundary the
// stage is picked up and synthesized; MOX keys at the boundary itself; and
// StageCommitMs into that silent lead-in the keyer takes the freshest eligible
// stage, because the reply to the slot that just ended cannot exist any
// earlier (see StageCommitMs).

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using Zeus.Contracts;
// TxAudioIngest / TxService / TxMicBlockResampler live in the project's root
// namespace Zeus.Server (folder ≠ namespace here, as in DigitalService).
using Zeus.Server;

namespace Zeus.Server.Hosting.Digital;

internal sealed class Ft8KeyerService : BackgroundService
{
    /// <summary>Nominal in-slot start of the FT8/FT4 signal (WSJT-X: 0.5 s).</summary>
    private const int TxStartDelayMs = 500;

    /// <summary>
    /// How far into the slot the keyer settles on WHICH message to send.
    ///
    /// The stage is picked up KeyLeadMs BEFORE the boundary so the waveform can
    /// be synthesised in time — but the reply to the slot that just ended does
    /// not exist yet at that instant: the DX stops transmitting exactly at our
    /// boundary, the decoder runs (~13 ms here), the frontend settles and posts,
    /// and the fresh stage lands a few hundred ms INTO our slot. Committing at
    /// the boundary therefore sent the PREVIOUS message and pushed the whole QSO
    /// a cycle late — the operator answered the DX's second copy, every time.
    ///
    /// So MOX keys at the boundary as always and the signal still starts at
    /// TxStartDelayMs; we simply take the freshest eligible stage at this point
    /// inside that silent lead-in. No RF timing moves.
    /// </summary>
    private const int StageCommitMs = 350;

    /// <summary>
    /// At <see cref="StageCommitMs"/> into a transmission that began on the
    /// boundary: has a fresher reply landed that should go out instead of the
    /// message picked up before the boundary? Same slot, same parity, a
    /// different message — the freshness rules are <see cref="TxStageBook"/>'s.
    /// </summary>
    internal static bool WantsFresherStage(TxStage keyed, TxStage? fresh, double boundaryMs)
    {
        if (fresh is null) return false;
        if (string.Equals(fresh.Message, keyed.Message, StringComparison.Ordinal)) return false;
        return TxStageBook.EligibleLate(
            fresh, SlotClock.SlotIndex(boundaryMs, fresh.Mode), boundaryMs, MaxLateStartMs(fresh.Mode));
    }

    /// <summary>
    /// How far into a slot we will still start transmitting. The reply to a
    /// decode is staged after the boundary by definition (the DX stops
    /// transmitting as our slot opens, and only then does the decoder run), so
    /// without this every answer slipped a whole cycle. Inside the nominal
    /// start delay nothing is lost at all; past it the waveform starts
    /// truncated, which receivers tolerate — WSJT-X decodes a late start of a
    /// couple of seconds. FT4's slot is half as long, so its window is too.
    /// </summary>
    private static int MaxLateStartMs(DigitalMode m) => m == DigitalMode.Ft4 ? 1_000 : 2_500;

    /// <summary>
    /// May this stage key the slot that is ALREADY running, and how much of the
    /// waveform has that slot already eaten?
    ///
    /// ORDERING IS LOAD-BEARING: the loop must ask this BEFORE it naps toward
    /// the next boundary. A decode-driven reply is staged one or two seconds
    /// into its own slot, when the next boundary is still most of a slot away —
    /// so a late-start test placed after the "nap until the lead window" guard
    /// is only ever evaluated at boundary minus <see cref="SlotClock.KeyLeadMs"/>,
    /// where the current slot is ~14.9 s old and the answer is always false.
    /// That is how the fix for the one-cycle-late reply shipped as dead code and
    /// the QSO kept slipping a cycle.
    /// </summary>
    internal static bool WantsLateStart(
        TxStage stage, double nowMs, double? lastTxSlotMs, out double slotStartMs, out int skipMs)
    {
        int slotMs = SlotClock.SlotMs(stage.Mode);
        long curIdx = (long)Math.Floor(nowMs / slotMs);
        slotStartMs = SlotClock.SlotStartMs(curIdx, stage.Mode);
        double intoSlot = nowMs - slotStartMs;
        int maxLate = MaxLateStartMs(stage.Mode);
        skipMs = 0;

        if (intoSlot <= 0 || intoSlot > maxLate) return false;
        // Never key the same slot twice.
        if (lastTxSlotMs is double last && Math.Abs(last - slotStartMs) <= 1.0) return false;
        if (!TxStageBook.EligibleLate(stage, curIdx, slotStartMs, maxLate)) return false;

        // Inside the nominal start delay we simply start on time.
        skipMs = (int)Math.Max(0, intoSlot - TxStartDelayMs);
        return true;
    }

    /// <summary>Watchdog slack past the waveform's own duration.</summary>
    private const int WatchdogSlackMs = 3_000;

    private const int IdlePollMs = 20;
    private static readonly TimeSpan MoxReleaseGuard = TimeSpan.FromMilliseconds(60);

    /// <summary>Headroom under the ingest limiter's ±0.95 clamp.</summary>
    private const float Amplitude = 0.90f;

    private static readonly int SampleRateHz = TxMicBlockResampler.OutputSampleRate;
    private static readonly int BlockSamples = TxMicBlockResampler.OutputBlockSamples;

    private readonly DigitalService _digital;
    private readonly TxAudioIngest _ingest;
    private readonly TxService _tx;
    private readonly ILogger<Ft8KeyerService> _log;

    private readonly float[] _block;
    private readonly byte[] _payload;

    public Ft8KeyerService(
        DigitalService digital,
        TxAudioIngest ingest,
        TxService tx,
        ILogger<Ft8KeyerService> log)
    {
        _digital = digital;
        _ingest = ingest;
        _tx = tx;
        _log = log;
        _block = new float[BlockSamples];
        _payload = new byte[BlockSamples * sizeof(float)];
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.Factory.StartNew(
            () => Loop(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private void Loop(CancellationToken ct)
    {
        Zeus.Protocol2.RealtimeThreadPriority.PromoteCallingThreadToProAudio(_log);
        _log.LogInformation("ft8 keyer: up (rate={Rate} Hz, block={Block})",
            SampleRateHz, BlockSamples);

        while (!ct.IsCancellationRequested)
        {
            if (!_digital.Armed)
            {
                if (ct.WaitHandle.WaitOne(IdlePollMs)) return;
                continue;
            }

            var stage = _digital.Stages.Peek();
            if (stage is null)
            {
                if (ct.WaitHandle.WaitOne(IdlePollMs)) return;
                continue;
            }

            int slotMs = SlotClock.SlotMs(stage.Mode);
            double now = _digital.Clock.UtcNowMs;
            long nextIdx = (long)Math.Floor(now / slotMs) + 1;
            double boundaryMs = SlotClock.SlotStartMs(nextIdx, stage.Mode);
            double msToBoundary = boundaryMs - now;

            // Is the CURRENT slot still keyable? A reply staged just after its
            // boundary belongs to this slot, not the next one of that parity.
            // This MUST come before the nap below — see WantsLateStart.
            if (WantsLateStart(stage, now, _digital.LastTxSlotMs, out double curStart, out int skipMs))
            {
                bool lateFt4 = stage.Mode == DigitalMode.Ft4;
                float[]? lateWave = Ft8Native.Synth(stage.Message, lateFt4, stage.AudioHz,
                                                    SampleRateHz, out string? lateErr);
                if (lateWave is null)
                {
                    _log.LogWarning("ft8 keyer: synth failed for '{Msg}': {Err}", stage.Message, lateErr);
                    _digital.Stages.Clear();
                    _digital.Events.PublishTxStatus(_digital.BuildTxStatus());
                    continue;
                }
                Transmit(lateWave, stage, curStart, ct, skipMs);
                continue;
            }

            if (msToBoundary > SlotClock.KeyLeadMs)
            {
                // Not our moment yet — nap, but wake before the lead window.
                int nap = (int)Math.Min(IdlePollMs, Math.Max(1, msToBoundary - SlotClock.KeyLeadMs));
                if (ct.WaitHandle.WaitOne(nap)) return;
                continue;
            }

            if (!TxStageBook.Eligible(stage, nextIdx, boundaryMs))
            {
                // Wrong parity (or stale) for THIS boundary — sleep just past it
                // and re-evaluate against the next one. Do NOT clear: the stage
                // may be waiting for its own parity.
                WaitUntil(boundaryMs + 5, ct, respectArm: false);
                continue;
            }

            // --- inside the lead window with an eligible stage: synthesize ---
            bool isFt4 = stage.Mode == DigitalMode.Ft4;
            float[]? wave = Ft8Native.Synth(stage.Message, isFt4, stage.AudioHz,
                                            SampleRateHz, out string? synthError);
            if (wave is null)
            {
                _log.LogWarning("ft8 keyer: synth failed for '{Msg}': {Err}",
                    stage.Message, synthError);
                _digital.Stages.Clear();
                _digital.Events.PublishTxStatus(_digital.BuildTxStatus());
                WaitUntil(boundaryMs + 5, ct, respectArm: false);
                continue;
            }

            // Hold for the boundary itself (disarm during the wait aborts).
            if (!WaitUntil(boundaryMs, ct, respectArm: true)) continue;
            if (ct.IsCancellationRequested) return;

            Transmit(wave, stage, boundaryMs, ct);
        }
    }

    /// <summary>Key MOX, stream the waveform paced in real time, unkey.</summary>
    private void Transmit(float[] wave, TxStage stage, double boundaryMs, CancellationToken ct,
                          int skipMs = 0)
    {
        if (!_tx.TrySetMox(true, MoxSource.Ft8Keyer, out var moxError))
        {
            _log.LogWarning("ft8 keyer: MOX refused: {Err}", moxError ?? "unknown");
            _digital.Stages.Clear();
            _digital.Events.PublishTxStatus(_digital.BuildTxStatus());
            return;
        }

        _digital.Transmitting = true;
        _digital.LastTxSlotMs = boundaryMs;
        _digital.Events.PublishTxStatus(_digital.BuildTxStatus());

        try
        {
            // Nominal in-slot signal start (abortable). A late start skips the
            // audio the slot has already consumed instead of delaying further.
            int waitMs = skipMs > 0 ? 0 : TxStartDelayMs;

            // Started on the boundary: spend the first part of the silent
            // lead-in waiting for the reply to the slot that just ended, then
            // send THAT instead. See StageCommitMs.
            if (waitMs > 0)
            {
                if (!WaitMs(StageCommitMs, ct) || !_digital.Armed) return;
                waitMs -= StageCommitMs;

                if (_digital.Stages.Peek() is { } fresh
                    && WantsFresherStage(stage, fresh, boundaryMs))
                {
                    float[]? freshWave = Ft8Native.Synth(
                        fresh.Message, fresh.Mode == DigitalMode.Ft4, fresh.AudioHz,
                        SampleRateHz, out string? freshErr);
                    if (freshWave is null)
                    {
                        // Keep the message we already have rather than dropping
                        // the slot: a stale reply beats silence.
                        _log.LogWarning("ft8 keyer: synth failed for the fresher '{Msg}': {Err}",
                            fresh.Message, freshErr);
                    }
                    else
                    {
                        _log.LogInformation("ft8 keyer: took the fresher reply '{New}' over '{Old}'",
                            fresh.Message, stage.Message);
                        stage = fresh;
                        wave = freshWave;
                        _digital.Events.PublishTxStatus(_digital.BuildTxStatus());
                    }
                }
            }

            if (skipMs > 0)
                _log.LogInformation("ft8 keyer: TX '{Msg}' @{Hz} Hz ({Mode}, slot {Slot}, late start {Late} ms)",
                    stage.Message, stage.AudioHz, stage.Mode, stage.Slot, skipMs);
            else
                _log.LogInformation("ft8 keyer: TX '{Msg}' @{Hz} Hz ({Mode}, slot {Slot})",
                    stage.Message, stage.AudioHz, stage.Mode, stage.Slot);

            int skipSamples = Math.Min(wave.Length, (int)(skipMs / 1000.0 * SampleRateHz));
            if ((waitMs == 0 || WaitMs(waitMs, ct)) && _digital.Armed)
                Pump(wave, ct, skipSamples);
        }
        finally
        {
            if (_tx.MoxOwner == MoxSource.Ft8Keyer)
            {
                try
                {
                    Thread.Sleep(MoxReleaseGuard); // let the last block drain
                    _tx.TrySetMox(false, MoxSource.Ft8Keyer, out _);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "ft8 keyer: MOX release failed");
                }
            }

            _digital.Transmitting = false;
            // One-shot: the runner re-stages every cycle while armed.
            _digital.Stages.Clear();
            _digital.Events.PublishTxStatus(_digital.BuildTxStatus());
        }
    }

    /// <summary>Stopwatch-paced block pump (the SignalJammer pattern).</summary>
    private void Pump(float[] wave, CancellationToken ct, int startSample = 0)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long periodTicks = (long)(System.Diagnostics.Stopwatch.Frequency
            * BlockSamples / (double)SampleRateHz);
        long nextDeadlineTicks = clock.ElapsedTicks;
        long watchdogTicks = (long)(System.Diagnostics.Stopwatch.Frequency
            * ((wave.Length / (double)SampleRateHz) + WatchdogSlackMs / 1000.0));

        int offset = startSample;
        while (offset < wave.Length)
        {
            if (ct.IsCancellationRequested) return;
            if (!_digital.Armed)
            {
                _log.LogInformation("ft8 keyer: halted mid-signal at {Sec:F1} s",
                    offset / (double)SampleRateHz);
                return;
            }
            if (clock.ElapsedTicks > watchdogTicks)
            {
                _log.LogError("ft8 keyer: WATCHDOG — transmission overran, aborting");
                return;
            }

            int take = Math.Min(BlockSamples, wave.Length - offset);
            for (int i = 0; i < take; i++)
            {
                float s = wave[offset + i] * Amplitude;
                _block[i] = float.IsFinite(s) ? Math.Clamp(s, -0.95f, 0.95f) : 0f;
            }
            for (int i = take; i < BlockSamples; i++) _block[i] = 0f;

            for (int i = 0; i < BlockSamples; i++)
                BinaryPrimitives.WriteSingleLittleEndian(
                    _payload.AsSpan(i * sizeof(float), sizeof(float)), _block[i]);
            _ingest.OnMicPcmBytesFromWav(new ReadOnlyMemory<byte>(_payload, 0, _payload.Length));

            offset += take;

            nextDeadlineTicks += periodTicks;
            long remainingTicks = nextDeadlineTicks - clock.ElapsedTicks;
            if (remainingTicks <= 0)
            {
                if (-remainingTicks > periodTicks * 8) nextDeadlineTicks = clock.ElapsedTicks;
                continue;
            }
            int delayMs = (int)(remainingTicks * 1000 / System.Diagnostics.Stopwatch.Frequency);
            if (delayMs > 0 && ct.WaitHandle.WaitOne(delayMs)) return;
        }
    }

    /// <summary>Sleep until the UTC-ms deadline on the digital clock.
    /// Returns false if cancelled (or, when respectArm, disarmed).</summary>
    private bool WaitUntil(double utcMs, CancellationToken ct, bool respectArm)
    {
        while (true)
        {
            double remain = utcMs - _digital.Clock.UtcNowMs;
            if (remain <= 0) return true;
            if (respectArm && !_digital.Armed) return false;
            int nap = remain > 50 ? 10 : 2;
            if (ct.WaitHandle.WaitOne(nap)) return false;
        }
    }

    private static bool WaitMs(int ms, CancellationToken ct)
        => !ct.WaitHandle.WaitOne(ms);
}
