// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// The settings check catalog — the common ways a radio ends up set wrong, and
// the setup / host / network conditions behind the problems owners report.
// Each check names the symptoms it is relevant to, reads facts, and returns a
// verdict with the actual value, a plain explanation and the exact fix in
// on-screen terms. The same results feed the problem report, the assistant's
// check_settings tool and the "Check my settings" button (which needs no AI
// and sends nothing anywhere).
//
// THRESHOLDS ARE DEFAULTS FOR REVIEW: they live in SettingsCheckThresholds so
// Apache Labs can tune them per model without touching the checks.

using System.Globalization;

namespace Zeus.Server.Diagnostics;

public enum SettingsVerdict
{
    Ok = 0,
    Check = 1,
    Likely = 2,
}

public sealed record SettingsCheckResult(
    string CheckId,
    string Title,
    SettingsVerdict Verdict,
    string Value,
    string Explanation,
    string Fix,
    string? ManualRef);

public sealed record SettingsCheck(
    string Id,
    IReadOnlyList<string> Symptoms,
    Func<SettingsFacts, IEnumerable<SettingsCheckResult>> Evaluate);

public static class SettingsCheckThresholds
{
    // Receive
    public const double AfLikelyDb = -40, AfCheckDb = -25;
    public const double AgcTopLikelyDb = 40, AgcTopCheckDb = 55;     // range 30..90, default 90
    public const double AttenLikelyDb = 20, AttenCheckDb = 10;
    public const int VoiceFilterLikelyHz = 300, CwFilterCheckHz = 50, DigitalFilterCheckHz = 2500;
    // Transmit
    public const double DriveLikelyPct = 10, DriveCheckPct = 30;
    public const double DriveCapLikelyPct = 50;
    public const double TunePctCheck = 5;
    public const double MicLowLikelyDb = -25, MicLowCheckDb = -15, MicHighLikelyDb = 6;
    public const int TxFilterCheckHz = 1800;
    public const double LevelerHighCheckDb = 15;
    // PureSignal feedback window (calcc): healthy ~100..180
    public const double PsFeedbackHot = 181, PsFeedbackLow = 90;
    // Host
    public const double CpuTempLikelyC = 80, CpuTempCheckC = 70;
    public const double DiskLikelyMb = 500, DiskCheckMb = 2000;
}

public static class SettingsCheckCatalog
{
    // Symptom sets (ids from SymptomRegistry).
    private static readonly string[] RxNoAudio = ["rx-no-audio"];
    private static readonly string[] RxAny = ["rx-no-audio", "rx-audio-quality", "rx-weak"];
    private static readonly string[] TxPower = ["no-tx-power"];
    private static readonly string[] TxAudio = ["tx-audio"];
    private static readonly string[] Ps = ["ps-not-working"];
    private static readonly string[] Connect = ["wont-connect"];
    private static readonly string[] Remote = ["remote-cant-connect", "remote-poor"];
    private static readonly string[] Digital = ["digital-modes"];
    private static readonly string[] Everything = [];

    private static readonly string[] VoiceModes = ["LSB", "USB", "AM", "SAM", "DSB", "FM"];
    private static readonly string[] SsbModes = ["LSB", "USB"];
    private static readonly string[] CwModes = ["CWL", "CWU"];
    private static readonly string[] DigiModes = ["DIGL", "DIGU"];

    public static IReadOnlyList<SettingsCheck> All { get; } = Build();

    /// <summary>Run every check relevant to a symptom (all of them for null / "other").</summary>
    public static IReadOnlyList<SettingsCheckResult> Evaluate(string? symptomId, SettingsFacts facts)
    {
        var all = symptomId is null or "" or "other";
        var results = new List<SettingsCheckResult>();
        foreach (var c in All)
        {
            if (!all && c.Symptoms.Count > 0 && !c.Symptoms.Contains(symptomId!)) continue;
            try
            {
                results.AddRange(c.Evaluate(facts));
            }
            catch
            {
                // A check must never break the report: a bad fact skips that check.
            }
        }
        return results
            .OrderByDescending(r => r.Verdict)
            .ThenBy(r => r.CheckId, StringComparer.Ordinal)
            .ToList();
    }

    private static string Rx(int i) => $"RX{i + 1}";
    private static string Db(double v) => v.ToString("0.#", CultureInfo.InvariantCulture) + " dB";
    private static string Pct(double v) => v.ToString("0", CultureInfo.InvariantCulture) + "%";

    private static SettingsCheckResult R(string id, string title, SettingsVerdict v, string value, string why, string fix, string? manual = null) =>
        new(id, title, v, value, why, fix, manual);

    private static IReadOnlyList<SettingsCheck> Build() =>
    [
        // ───────────── Receive ─────────────
        new("rx.muted", RxNoAudio, f => f.Receivers().Select(i =>
        {
            var muted = f.Bool($"receivers[{i}].muted") ?? (i == 0 ? f.Bool("muted") : null) ?? false;
            return R("rx.muted", $"{Rx(i)} mute", muted ? SettingsVerdict.Likely : SettingsVerdict.Ok,
                muted ? "muted" : "not muted",
                $"{Rx(i)} is muted, so it makes no sound however strong the signal.",
                $"Turn {Rx(i)}'s MUTE off (MUTE key, or CTRL → MUTE on its pane).", "Receiver controls");
        })),

        new("rx.af", RxNoAudio, f => f.Receivers().SelectMany(i =>
        {
            var af = f.Num($"receivers[{i}].afGainDb") ?? (i == 0 ? f.Num("rxAfGainDb") : null);
            if (af is not double v) return Array.Empty<SettingsCheckResult>();
            var verdict = v <= SettingsCheckThresholds.AfLikelyDb ? SettingsVerdict.Likely
                : v <= SettingsCheckThresholds.AfCheckDb ? SettingsVerdict.Check : SettingsVerdict.Ok;
            return new[] { R("rx.af", $"{Rx(i)} AF gain", verdict, Db(v),
                $"{Rx(i)}'s AF gain sets the listening volume; at {Db(v)} audio is very quiet.",
                $"Raise {Rx(i)}'s AF (CTRL → AF, or the AF knob).", "Receiver controls") };
        })),

        new("rx.agcT", ["rx-no-audio", "rx-weak"], f => f.Receivers().SelectMany(i =>
        {
            var top = f.Num($"receivers[{i}].agcTopDb") ?? (i == 0 ? f.Num("agcTopDb") : null);
            if (top is not double t) return Array.Empty<SettingsCheckResult>();
            var auto = (f.Bool($"receivers[{i}].autoAgcEnabled") ?? (i == 0 ? f.Bool("autoAgcEnabled") : null)) == true;
            if (auto)
            {
                return new[] { R("rx.agcT", $"{Rx(i)} AGC-T", SettingsVerdict.Ok, $"{Db(t)} · auto",
                    "Auto AGC-T is following the band's noise floor.", "—", "Receiver controls") };
            }
            var verdict = t <= SettingsCheckThresholds.AgcTopLikelyDb ? SettingsVerdict.Likely
                : t <= SettingsCheckThresholds.AgcTopCheckDb ? SettingsVerdict.Check : SettingsVerdict.Ok;
            return new[] { R("rx.agcT", $"{Rx(i)} AGC-T", verdict, Db(t),
                $"AGC-T is the receiver's maximum gain (30–90 dB, default 90). At {Db(t)} the gain is capped, so weak and average signals come out very quiet.",
                $"Raise {Rx(i)}'s AGC-T (CTRL → AGC-T, or the AGC slider) until the noise just starts to rise, or turn on Auto AGC-T.",
                "Receiver controls › AGC-T") };
        })),

        new("rx.agcMode", ["rx-no-audio", "rx-audio-quality"], f =>
        {
            var mode = f.Str("agc.mode");
            if (mode is null) return Array.Empty<SettingsCheckResult>();
            var fixedMode = string.Equals(mode, "Fixed", StringComparison.OrdinalIgnoreCase);
            return new[] { R("rx.agcMode", "AGC mode", fixedMode ? SettingsVerdict.Check : SettingsVerdict.Ok, mode,
                "With AGC Fixed, loudness depends entirely on AGC-T and the signal; strong signals can be loud and weak ones near silent.",
                "Choose Medium (or Slow/Fast) AGC unless you need Fixed.", "Receiver controls") };
        }),

        new("rx.squelch", RxNoAudio, f =>
        {
            var on = f.Bool("squelch.enabled");
            if (on is null) return Array.Empty<SettingsCheckResult>();
            return new[] { R("rx.squelch", "Squelch", on == true ? SettingsVerdict.Check : SettingsVerdict.Ok,
                on == true ? $"on (level {f.Num("squelch.level") ?? 0})" : "off",
                "Squelch holds the audio closed until a signal is strong enough to open it.",
                "Turn squelch off, or lower its level.", "Receiver controls") };
        }),

        new("rx.atten", ["rx-no-audio", "rx-weak"], f =>
        {
            var att = f.Num("attenDb");
            if (att is not double a) return Array.Empty<SettingsCheckResult>();
            var offset = f.Bool("autoAttEnabled") == true ? (f.Num("attOffsetDb") ?? 0) : 0;
            var total = a + offset;
            var verdict = total >= SettingsCheckThresholds.AttenLikelyDb ? SettingsVerdict.Likely
                : total >= SettingsCheckThresholds.AttenCheckDb ? SettingsVerdict.Check : SettingsVerdict.Ok;
            return new[] { R("rx.atten", "Attenuator", verdict, offset != 0 ? $"{Db(a)} + auto {Db(offset)}" : Db(a),
                "Attenuation lowers everything reaching the receiver — useful against overload, but it buries weak signals.",
                "Reduce ATT (or let auto-attenuation settle) unless the ADC overload warning is showing.", "Receiver controls") };
        }),

        new("rx.filter", ["rx-no-audio", "rx-weak", "digital-modes"], f => f.Receivers().SelectMany(i =>
        {
            var mode = f.Str($"receivers[{i}].mode") ?? (i == 0 ? f.Str("mode") : null);
            var lo = f.Num($"receivers[{i}].filterLowHz") ?? (i == 0 ? f.Num("filterLowHz") : null);
            var hi = f.Num($"receivers[{i}].filterHighHz") ?? (i == 0 ? f.Num("filterHighHz") : null);
            if (mode is null || lo is not double l || hi is not double h) return Array.Empty<SettingsCheckResult>();
            var width = Math.Abs(h - l);
            var verdict = SettingsVerdict.Ok;
            if (VoiceModes.Contains(mode) && width < SettingsCheckThresholds.VoiceFilterLikelyHz) verdict = SettingsVerdict.Likely;
            else if (CwModes.Contains(mode) && width < SettingsCheckThresholds.CwFilterCheckHz) verdict = SettingsVerdict.Check;
            else if (DigiModes.Contains(mode) && width < SettingsCheckThresholds.DigitalFilterCheckHz) verdict = SettingsVerdict.Check;
            return new[] { R("rx.filter", $"{Rx(i)} filter", verdict, $"{width:0} Hz in {mode}",
                "A receive filter much narrower than the signal cuts most of it out (FT8 needs about 2.5–3 kHz).",
                "Pick a wider preset on the FILTER card (e.g. 2.7 kHz SSB, 3 kHz digital).", "Filters") };
        })),

        new("rx.adcOverload", ["rx-audio-quality", "rx-weak"], f =>
        {
            var ov = f.Bool("adcOverloadWarning");
            if (ov is null) return Array.Empty<SettingsCheckResult>();
            return new[] { R("rx.adcOverload", "ADC overload", ov == true ? SettingsVerdict.Likely : SettingsVerdict.Ok,
                ov == true ? "overloading" : "no overload",
                "The receiver's converter is being driven too hard; this causes distortion, spurious signals and a raised noise floor.",
                "Add attenuation (ATT) or turn on auto-attenuation; check for a nearby strong transmitter.", "Receiver controls") };
        }),

        new("rx.audioDevice", RxNoAudio, f =>
        {
            var active = f.X("dsp-audio.audio.activeDeviceId");
            var configured = f.X("dsp-audio.audio.configuredDeviceId");
            var sinkMuted = f.XBool("dsp-audio.audio.muted");
            var list = new List<SettingsCheckResult>();
            if (sinkMuted is bool m)
            {
                list.Add(R("rx.audioMuted", "Audio output", m ? SettingsVerdict.Likely : SettingsVerdict.Ok, m ? "muted" : "playing",
                    "The audio output itself is muted, separately from the receivers.", "Unmute the audio output (the speaker/MUTE control in the top bar).", "Audio"));
            }
            if (!string.IsNullOrEmpty(configured) && !string.IsNullOrEmpty(active) &&
                !string.Equals(active, configured, StringComparison.Ordinal))
            {
                list.Add(R("rx.audioDevice", "Audio device", SettingsVerdict.Likely, $"playing on \"{active}\", configured \"{configured}\"",
                    "Audio is going to a different output than the one chosen — often a headset or HDMI monitor that was plugged in or out.",
                    "Pick the right output again in Settings → Audio, or reconnect the device.", "Audio"));
            }
            return list;
        }),

        new("rx.nr", ["rx-audio-quality"], f =>
        {
            var nr = f.Str("nr.mode") ?? f.Str("nr");
            if (nr is null) return Array.Empty<SettingsCheckResult>();
            var on = !nr.Contains("Off", StringComparison.OrdinalIgnoreCase);
            return new[] { R("rx.nr", "Noise reduction", on ? SettingsVerdict.Check : SettingsVerdict.Ok, nr,
                "Strong noise reduction can make voices sound watery, hollow or chopped.",
                "Try a lighter NR, or NR off, and compare.", "Noise reduction") };
        }),

        // ───────────── Transmit ─────────────
        new("tx.drive", ["no-tx-power", "digital-modes"], f =>
        {
            var d = f.Num("drivePct");
            if (d is not double v) return Array.Empty<SettingsCheckResult>();
            var verdict = v <= SettingsCheckThresholds.DriveLikelyPct ? SettingsVerdict.Likely
                : v <= SettingsCheckThresholds.DriveCheckPct ? SettingsVerdict.Check : SettingsVerdict.Ok;
            return new[] { R("tx.drive", "Drive", verdict, Pct(v),
                "Drive sets how hard the transmitter is driven; low drive means low output on every mode.",
                "Raise DRV (CTRL → DRV, or the drive slider).", "Transmitting") };
        }),

        new("tx.driveCap", TxPower, f =>
        {
            var cap = f.Num("driveMaxPct");
            if (cap is not double c) return Array.Empty<SettingsCheckResult>();
            if (c >= 100)
            {
                return new[] { R("tx.driveCap", "Drive limit", SettingsVerdict.Ok, Pct(c), "Drive is not capped.", "—", "PA settings") };
            }
            return new[] { R("tx.driveCap", "Drive limit", c <= SettingsCheckThresholds.DriveCapLikelyPct ? SettingsVerdict.Likely : SettingsVerdict.Check, Pct(c),
                $"The drive limit caps output at {Pct(c)} of drive, whatever the DRV slider says.",
                "Raise the drive limit in Settings → PA if you meant to allow full power.", "PA settings") };
        }),

        new("tx.tune", TxPower, f =>
        {
            var t = f.Num("tunePct");
            if (t is not double v) return Array.Empty<SettingsCheckResult>();
            return new[] { R("tx.tune", "TUN power", v <= SettingsCheckThresholds.TunePctCheck ? SettingsVerdict.Check : SettingsVerdict.Ok, Pct(v),
                "TUN uses its own power setting; if you are judging output with TUN, a low TUN power looks like a weak transmitter.",
                "Raise the TUN power if you are testing with TUN (it is deliberately low for tuning).", "Transmitting") };
        }),

        new("tx.mic", ["no-tx-power", "tx-audio"], f =>
        {
            var mode = f.Str("mode");
            var mic = f.Num("micGainDb");
            if (mic is not double m || (mode is not null && !VoiceModes.Contains(mode))) return Array.Empty<SettingsCheckResult>();
            var verdict = m <= SettingsCheckThresholds.MicLowLikelyDb ? SettingsVerdict.Likely
                : m <= SettingsCheckThresholds.MicLowCheckDb ? SettingsVerdict.Check
                : m >= SettingsCheckThresholds.MicHighLikelyDb ? SettingsVerdict.Likely : SettingsVerdict.Ok;
            var high = m >= SettingsCheckThresholds.MicHighLikelyDb;
            return new[] { R("tx.mic", "Mic gain", verdict, Db(m),
                high ? "Mic gain this high overdrives the audio chain — distorted, splattery transmit audio."
                     : "Low mic gain gives weak modulation, so voice output stays low even at full drive.",
                high ? "Lower MIC (CTRL → MIC) until peaks just reach the ALC." : "Raise MIC (CTRL → MIC) while watching the mic and ALC meters.",
                "Transmitting › Mic") };
        }),

        new("tx.audioSource", ["tx-audio", "digital-modes", "no-tx-power"], f =>
        {
            var src = f.Str("txAudioSource");
            if (src is null) return Array.Empty<SettingsCheckResult>();
            return new[] { R("tx.audioSource", "TX audio source", SettingsVerdict.Check, src.Trim('"'),
                "The transmitter only hears the selected source; speaking into a mic while PC/remote audio is selected (or the reverse) transmits silence.",
                "Make sure the TX audio source matches what you are using (Settings → Audio Tools).", "Transmit audio") };
        }),

        new("tx.filter", ["no-tx-power", "tx-audio"], f =>
        {
            var mode = f.Str("mode");
            var lo = f.Num("txFilterLowHz");
            var hi = f.Num("txFilterHighHz");
            if (mode is null || !SsbModes.Contains(mode) || lo is not double l || hi is not double h) return Array.Empty<SettingsCheckResult>();
            var width = Math.Abs(h - l);
            return new[] { R("tx.filter", "TX filter", width < SettingsCheckThresholds.TxFilterCheckHz ? SettingsVerdict.Check : SettingsVerdict.Ok, $"{width:0} Hz",
                "A narrow TX filter removes much of the voice energy, which lowers average output and makes audio thin.",
                "Use a 2.4–2.8 kHz TX filter for normal SSB.", "Transmit audio") };
        }),

        new("tx.processor", ["no-tx-power", "tx-audio"], f =>
        {
            var mode = f.Str("mode");
            var comp = f.Bool("txLeveling.compressorEnabled");
            if (comp is null || (mode is not null && !VoiceModes.Contains(mode))) return Array.Empty<SettingsCheckResult>();
            var cessb = f.Bool("txLeveling.cessbEnabled") ?? true;
            var gain = f.Num("txLeveling.compressorGainDb") ?? 0;
            if (comp == false)
            {
                return new[] { R("tx.processor", "Speech processor", SettingsVerdict.Check, "off",
                    "Without the speech processor, voice peaks limit the average power — SSB talk power stays low even at full drive.",
                    "Turn the compressor on (Settings → TX, about 6–10 dB) with CESSB on, which keeps the peaks clean.",
                    "Transmit audio › Speech processor") };
            }
            if (!cessb)
            {
                return new[] { R("tx.processor", "Speech processor", SettingsVerdict.Check, $"COMP {gain:0} dB · CESSB off",
                    "The compressor is running without CESSB, so its peaks overshoot after filtering and the ALC has to hold the level down.",
                    "Turn CESSB on next to the compressor.", "Transmit audio › Speech processor") };
            }
            return new[] { R("tx.processor", "Speech processor", SettingsVerdict.Ok, $"COMP {gain:0} dB · CESSB on",
                "The compressor and CESSB are both running.", "—", "Transmit audio › Speech processor") };
        }),

        new("tx.leveler", TxAudio, f =>
        {
            var g = f.Num("levelerMaxGainDb");
            if (g is not double v) return Array.Empty<SettingsCheckResult>();
            return new[] { R("tx.leveler", "Leveler max gain", v >= SettingsCheckThresholds.LevelerHighCheckDb ? SettingsVerdict.Check : SettingsVerdict.Ok, Db(v),
                "A high leveler gain lifts background noise between words and can make audio pump.",
                "Lower the leveler max gain in the DSP / TX leveling settings.", "Transmit audio") };
        }),

        // ───────────── PureSignal ─────────────
        new("ps.state", ["ps-not-working", "tx-audio"], f =>
        {
            var enabled = f.Bool("psEnabled");
            if (enabled is null) return Array.Empty<SettingsCheckResult>();
            var list = new List<SettingsCheckResult>();
            if (enabled == false)
            {
                list.Add(R("ps.enabled", "PureSignal", SettingsVerdict.Check, "off",
                    "PureSignal is not armed, so it cannot calibrate.", "Turn PS on (PS key) and key a two-tone.", "PureSignal"));
                return list;
            }
            var stalled = f.Bool("psCalibrationStalled") == true;
            list.Add(R("ps.stalled", "PS calibration", stalled ? SettingsVerdict.Likely : SettingsVerdict.Ok, stalled ? "stalled" : "running",
                "PureSignal keeps refusing its fits, so no correction is applied.", "See the feedback level check below; key a steady two-tone at normal power.", "PureSignal"));
            if (f.Num("psFeedbackLevel") is double fb && fb > 0)
            {
                var autoAtt = f.Bool("psAutoAttenuate") == true;
                var verdict = fb > SettingsCheckThresholds.PsFeedbackHot ? (autoAtt ? SettingsVerdict.Check : SettingsVerdict.Likely)
                    : fb < SettingsCheckThresholds.PsFeedbackLow ? SettingsVerdict.Check : SettingsVerdict.Ok;
                list.Add(R("ps.feedback", "PS feedback level", verdict, fb.ToString("0", CultureInfo.InvariantCulture),
                    fb > SettingsCheckThresholds.PsFeedbackHot ? "The feedback is too hot (healthy is about 100–180), so the calibrator rejects it."
                        : "The feedback is weak (healthy is about 100–180), so fits are unreliable.",
                    fb > SettingsCheckThresholds.PsFeedbackHot ? "Raise Feedback atten by 6–9 dB, or turn on Auto-attenuate." : "Lower Feedback atten, or check the coupler / feedback path.",
                    "PureSignal › Feedback level"));
            }
            var hw = f.X("tx-ps.ps.hwPeak");
            var hwDef = f.X("tx-ps.ps.hwPeakDefault");
            if (hw is not null && hwDef is not null && hw != hwDef)
            {
                list.Add(R("ps.hwPeak", "PS HW peak", SettingsVerdict.Check, $"{hw} (default {hwDef})",
                    "The HW peak differs from this radio's default; a wrong value makes the feedback read too hot or too quiet.",
                    "Reset HW peak to the default unless you set it on purpose.", "PureSignal"));
            }
            return list;
        }),

        // ───────────── Connection ─────────────
        new("conn.status", ["wont-connect", "remote-cant-connect", "rx-no-audio"], f =>
        {
            var status = f.Str("status");
            if (status is null) return Array.Empty<SettingsCheckResult>();
            var connected = string.Equals(status, "Connected", StringComparison.OrdinalIgnoreCase);
            return new[] { R("conn.status", "Radio connection", connected ? SettingsVerdict.Ok : SettingsVerdict.Likely,
                connected ? $"connected ({f.Str("endpoint") ?? "?"})" : status,
                "ANAN Core is not connected to the radio hardware, so there is no receive or transmit.",
                "Open the DISC panel and connect; if nothing is found, check the network cable/switch and that the radio is powered.",
                "Connecting to your radio") };
        }),

        new("conn.p2app", ["wont-connect", "rx-no-audio", "no-tx-power"], f =>
        {
            var mode = f.X("p2app.mode");
            if (mode is null) return Array.Empty<SettingsCheckResult>();
            var restarts = f.XNum("p2app.restarts") ?? 0;
            return mode switch
            {
                "NoBinary" => new[] { R("conn.p2app", "p2app", SettingsVerdict.Likely, "not installed",
                    "p2app (the Saturn's Protocol-2 bridge) is not installed on this host, so the radio is not reachable over the network.",
                    "Run the factory install or Settings → Updates → p2app at the radio.", "p2app") },
                "Paused" => new[] { R("conn.p2app", "p2app", SettingsVerdict.Likely, "stopped",
                    "p2app is stopped, so the radio is not streaming.", "Start p2app at the radio (Settings → Updates → p2app → START).", "p2app") },
                "Backoff" => new[] { R("conn.p2app", "p2app", SettingsVerdict.Likely, $"restarting ({restarts:0} restarts)",
                    "p2app keeps exiting and being restarted, so the connection keeps dropping.",
                    "Update p2app at the radio; if it continues, send a problem report with the log.", "p2app") },
                "Disabled" => Array.Empty<SettingsCheckResult>(),
                _ => new[] { R("conn.p2app", "p2app", SettingsVerdict.Ok, mode, "p2app is running.", "—", "p2app") },
            };
        }),

        new("conn.portInUse", ["wont-connect", "remote-cant-connect"], f =>
        {
            var hit = f.LastLog("address already in use");
            if (hit < 0) return Array.Empty<SettingsCheckResult>();
            return new[] { R("conn.portInUse", "Network port", SettingsVerdict.Likely, "in use by another program",
                "Another program (often a second copy of ANAN Core, piHPSDR, Thetis or p2app started by hand) holds a port ANAN Core needs.",
                "Close the other program or the duplicate copy, then restart ANAN Core.", "Troubleshooting") };
        }),

        // ───────────── Remote operation ─────────────
        new("remote.armed", ["remote-cant-connect"], f =>
        {
            var pwd = f.XBool("remote.passwordSet");
            var call = f.XBool("remote.callsignSet");
            var list = new List<SettingsCheckResult>();
            if (pwd is bool p)
            {
                list.Add(R("remote.password", "Remote password", p ? SettingsVerdict.Ok : SettingsVerdict.Likely, p ? "set" : "not set",
                    "Remote access is armed only when a remote password is set; without one the radio refuses every remote connection.",
                    "Set a remote password at the radio (Settings → Network → Remote).", "Remote operation"));
            }
            if (call is bool c)
            {
                list.Add(R("remote.callsign", "Remote callsign", c ? SettingsVerdict.Ok : SettingsVerdict.Likely,
                    c ? (f.X("remote.callsign") ?? "set") : "not set",
                    "The radio announces itself to the remote service under its callsign; with none set, the /go/<CALL> link cannot find it.",
                    "Set the remote callsign at the radio (Settings → Network → Remote).", "Remote operation"));
            }
            return list;
        }),

        new("remote.broker", ["remote-cant-connect"], f =>
        {
            var refused = f.LastLog("remote broker refused");
            var online = f.LastLog("host online for");
            var ended = f.LastLog("remote broker connection ended");
            if (refused >= 0 && refused > online)
            {
                return new[] { R("remote.claimed", "Remote callsign claim", SettingsVerdict.Likely, "claimed by another radio",
                    "The remote service refused this radio: the callsign is registered to a different radio's key (often a reinstall of this same radio).",
                    "Use a different remote callsign, or wait for the old claim to lapse (about 60 days offline). Apache Labs support can help.",
                    "Remote operation") };
            }
            if (ended >= 0 && ended > online)
            {
                return new[] { R("remote.broker", "Remote service", SettingsVerdict.Likely, "can't reach it",
                    "The radio keeps losing its connection to the remote service, so remote clients cannot find it.",
                    "Check the radio's internet connection (router, DNS, firewall blocking outbound HTTPS/WebSockets).",
                    "Remote operation") };
            }
            if (online >= 0)
            {
                return new[] { R("remote.broker", "Remote service", SettingsVerdict.Ok, "online",
                    "The radio is registered with the remote service.", "—", "Remote operation") };
            }
            return Array.Empty<SettingsCheckResult>();
        }),

        new("remote.load", ["remote-poor"], f =>
        {
            var list = new List<SettingsCheckResult>();
            list.Add(R("remote.bandwidth", "Remote link", SettingsVerdict.Check, "see the link chip",
                "Choppy audio and lag on remote are almost always the network path: Wi-Fi, mobile data, or a relayed (TURN) connection.",
                "Use wired or 5 GHz Wi-Fi at both ends where possible; the link chip shows whether the session is direct or relayed.",
                "Remote operation"));
            return list;
        }),

        // ───────────── Digital modes ─────────────
        new("digital.clock", ["digital-modes", "remote-cant-connect"], f =>
        {
            var synced = f.XBool("host.clockSynced");
            if (synced is null) return Array.Empty<SettingsCheckResult>();
            return new[] { R("digital.clock", "Clock sync", synced == true ? SettingsVerdict.Ok : SettingsVerdict.Likely,
                synced == true ? "synchronised" : "not synchronised",
                "FT8/FT4 need the clock within about a second, and secure connections fail when the clock is badly wrong.",
                "Connect the radio to the internet (or a GPS time source) so its clock can synchronise.", "Digital modes") };
        }),

        new("digital.mode", Digital, f =>
        {
            var mode = f.Str("mode");
            if (mode is null) return Array.Empty<SettingsCheckResult>();
            var ok = DigiModes.Contains(mode) || mode == "USB";
            return new[] { R("digital.mode", "Mode", ok ? SettingsVerdict.Ok : SettingsVerdict.Check, mode,
                "Digital-mode software expects USB or DIGU (LSB/DIGL on some setups).", "Select DIGU (or USB).", "Digital modes") };
        }),

        // ───────────── Host (all symptoms) ─────────────
        new("host.power", Everything, f =>
        {
            var uv = f.XBool("host.underVoltage");
            if (uv is null) return Array.Empty<SettingsCheckResult>();
            return new[] { R("host.power", "Power supply (Pi)", uv == true ? SettingsVerdict.Likely : SettingsVerdict.Ok,
                uv == true ? "under-voltage detected" : "OK",
                "The computer inside the radio has seen its supply voltage drop; this causes dropouts, network and USB glitches, and random crashes.",
                "Check the radio's DC supply and cabling; contact Apache Labs support if it persists.", "Troubleshooting") };
        }),

        new("host.thermal", Everything, f =>
        {
            var t = f.XNum("host.cpuTempC");
            var throttled = f.XBool("host.throttled") == true;
            if (t is null && !throttled) return Array.Empty<SettingsCheckResult>();
            var temp = t ?? 0;
            var verdict = throttled || temp >= SettingsCheckThresholds.CpuTempLikelyC ? SettingsVerdict.Likely
                : temp >= SettingsCheckThresholds.CpuTempCheckC ? SettingsVerdict.Check : SettingsVerdict.Ok;
            return new[] { R("host.thermal", "CPU temperature", verdict,
                (t is null ? "" : temp.ToString("0", CultureInfo.InvariantCulture) + " °C") + (throttled ? " · throttling" : ""),
                "A hot or throttling CPU makes the display stutter and can cause audio gaps.",
                "Make sure the radio's vents and fan are clear and the room is not hot.", "Troubleshooting") };
        }),

        new("host.disk", Everything, f =>
        {
            var free = f.XNum("host.diskFreeMb");
            if (free is not double mb) return Array.Empty<SettingsCheckResult>();
            var verdict = mb < SettingsCheckThresholds.DiskLikelyMb ? SettingsVerdict.Likely
                : mb < SettingsCheckThresholds.DiskCheckMb ? SettingsVerdict.Check : SettingsVerdict.Ok;
            return new[] { R("host.disk", "Free storage", verdict, mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB",
                "With little free space, settings may fail to save and updates and recordings fail.",
                "Delete old recordings, or free space on the radio's storage.", "Troubleshooting") };
        }),
    ];
}
