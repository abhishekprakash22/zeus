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
// SettingsCheckService — gathers the facts once (radio state, the diagnostic
// probes, host health, remote setup, recent log) and runs the catalog for a
// symptom. Used by the problem report (as a section + findings), by
// GET /api/diagnostics/check (the "Check my settings" button — local, no AI)
// and by the assistant's check_settings tool. Read-only throughout.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Zeus.Server.Diagnostics;

public sealed class SettingsCheckService
{
    private static readonly JsonSerializerOptions StateJson = CreateStateJson();

    private readonly IServiceProvider _services;
    private readonly IEnumerable<IDiagnosticProbe> _probes;
    private readonly DiagnosticLogBuffer _log;
    private readonly ILogger<SettingsCheckService> _logger;

    public SettingsCheckService(
        IServiceProvider services,
        IEnumerable<IDiagnosticProbe> probes,
        DiagnosticLogBuffer log,
        ILogger<SettingsCheckService> logger)
    {
        _services = services;
        _probes = probes;
        _log = log;
        _logger = logger;
    }

    /// <summary>
    /// Run the checks for a symptom. Pass the report's already-collected probe
    /// sections to avoid collecting twice; otherwise every probe runs here.
    /// </summary>
    public IReadOnlyList<SettingsCheckResult> Run(string? symptomId, IReadOnlyList<DiagnosticSection>? sections = null)
    {
        IReadOnlyList<string> recent;
        try
        {
            recent = _log.Snapshot(DiagnosticLogBuffer.Capacity);
        }
        catch
        {
            recent = Array.Empty<string>();
        }

        var extra = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in sections ?? CollectProbes(recent))
        {
            foreach (var kv in s.Items) extra[$"{s.Id}.{kv.Key}"] = kv.Value;
        }
        foreach (var kv in HostFacts.Collect()) extra[kv.Key] = kv.Value;
        foreach (var kv in RemoteFacts()) extra[kv.Key] = kv.Value;
        foreach (var kv in P2AppFacts()) extra[kv.Key] = kv.Value;

        JsonNode? state = null;
        try
        {
            if (_services.GetService(typeof(RadioService)) is RadioService radio)
            {
                state = JsonSerializer.SerializeToNode(radio.Snapshot(), StateJson);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "settings.check state snapshot failed");
        }

        return SettingsCheckCatalog.Evaluate(symptomId, new SettingsFacts(state, extra, recent));
    }

    /// <summary>The report section: every check with its verdict and value.</summary>
    public static DiagnosticSection ToSection(IReadOnlyList<SettingsCheckResult> results) =>
        new("settings-check", "Settings check",
            results.Select(r => new DiagnosticKeyValue(r.Title, $"{VerdictLabel(r.Verdict)} · {r.Value}")).ToList());

    /// <summary>Likely causes become report findings, with the fix.</summary>
    public static IEnumerable<DiagnosticFinding> ToFindings(IReadOnlyList<SettingsCheckResult> results) =>
        results.Where(r => r.Verdict == SettingsVerdict.Likely).Select(r => new DiagnosticFinding(
            $"{r.Title}: {r.Value}",
            $"{r.Explanation} Fix: {r.Fix}",
            DiagnosticSeverity.Likely,
            r.ManualRef is null ? null : "Operator manual › " + r.ManualRef));

    /// <summary>Compact text for the assistant: likely causes first, then the rest.</summary>
    public static string ToText(IReadOnlyList<SettingsCheckResult> results)
    {
        if (results.Count == 0) return "No settings checks apply to this symptom on this radio.";
        var lines = results.Select(r =>
            $"[{VerdictLabel(r.Verdict)}] {r.Title} = {r.Value}" +
            (r.Verdict == SettingsVerdict.Ok ? "" : $" — {r.Explanation} Fix: {r.Fix}") +
            (r.ManualRef is null ? "" : $" (manual: {r.ManualRef})"));
        return string.Join('\n', lines);
    }

    public static string VerdictLabel(SettingsVerdict v) => v switch
    {
        SettingsVerdict.Likely => "LIKELY CAUSE",
        SettingsVerdict.Check => "CHECK",
        _ => "OK",
    };

    private IEnumerable<DiagnosticSection> CollectProbes(IReadOnlyList<string> recent)
    {
        var ctx = new DiagnosticContext { Services = _services, RecentLog = recent };
        var list = new List<DiagnosticSection>();
        foreach (var p in _probes)
        {
            try
            {
                list.Add(p.Collect(ctx));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "settings.check probe {Probe} threw", p.Id);
            }
        }
        return list;
    }

    private IEnumerable<KeyValuePair<string, string>> RemoteFacts()
    {
        var list = new List<KeyValuePair<string, string>>();
        try
        {
            if (_services.GetService(typeof(Zeus.Server.Hosting.Remote.RemotePasswordStore)) is Zeus.Server.Hosting.Remote.RemotePasswordStore pwd)
            {
                list.Add(new("remote.passwordSet", pwd.HasPassword() ? "true" : "false"));
            }
            var env = Environment.GetEnvironmentVariable("ZEUS_REMOTE_CALLSIGN");
            string? call = string.IsNullOrWhiteSpace(env) ? null : env.Trim();
            if (call is null &&
                _services.GetService(typeof(Zeus.Server.Hosting.Remote.RemoteCallsignStore)) is Zeus.Server.Hosting.Remote.RemoteCallsignStore cs)
            {
                call = cs.Get();
            }
            list.Add(new("remote.callsignSet", string.IsNullOrWhiteSpace(call) ? "false" : "true"));
            if (!string.IsNullOrWhiteSpace(call)) list.Add(new("remote.callsign", call));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "settings.check remote facts unavailable");
        }
        return list;
    }

    private IEnumerable<KeyValuePair<string, string>> P2AppFacts()
    {
        var list = new List<KeyValuePair<string, string>>();
        try
        {
            if (_services.GetService(typeof(P2AppSupervisor)) is P2AppSupervisor sup)
            {
                var node = JsonSerializer.SerializeToNode(sup.Status(), StateJson);
                if (node?["mode"] is JsonNode m) list.Add(new("p2app.mode", m.ToString()));
                if (node?["restarts"] is JsonNode r) list.Add(new("p2app.restarts", r.ToJsonString()));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "settings.check p2app facts unavailable");
        }
        return list;
    }

    private static JsonSerializerOptions CreateStateJson()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}

/// <summary>
/// Host health facts, best effort and Linux-only where the source is Linux:
/// Raspberry Pi throttle flags (vcgencmd's get_throttled, read from sysfs),
/// CPU temperature, free space on the prefs volume, and systemd time sync.
/// Missing sources simply produce no fact, so the checks skip themselves.
/// </summary>
public static class HostFacts
{
    public const string PiThrottledPath = "/sys/devices/platform/soc/soc:firmware/get_throttled";
    public const string ThermalPath = "/sys/class/thermal/thermal_zone0/temp";
    public const string TimeSyncedPath = "/run/systemd/timesync/synchronized";
    public const string TimeSyncDir = "/run/systemd/timesync";

    public static IReadOnlyDictionary<string, string> Collect()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(PiThrottledPath))
            {
                foreach (var kv in ParseThrottled(File.ReadAllText(PiThrottledPath))) d[kv.Key] = kv.Value;
            }
        }
        catch { /* not a Pi, or no access */ }
        try
        {
            if (File.Exists(ThermalPath) &&
                double.TryParse(File.ReadAllText(ThermalPath).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var milli))
            {
                d["host.cpuTempC"] = (milli / 1000.0).ToString("0.#", CultureInfo.InvariantCulture);
            }
        }
        catch { /* no thermal zone */ }
        try
        {
            var dir = Path.GetDirectoryName(PrefsDbPath.Get());
            var root = string.IsNullOrEmpty(dir) ? null : Path.GetPathRoot(Path.GetFullPath(dir));
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    d["host.diskFreeMb"] = (drive.AvailableFreeSpace / (1024.0 * 1024.0)).ToString("0", CultureInfo.InvariantCulture);
                }
            }
        }
        catch { /* unusual filesystem */ }
        try
        {
            if (OperatingSystem.IsLinux() && Directory.Exists(TimeSyncDir))
            {
                d["host.clockSynced"] = File.Exists(TimeSyncedPath) ? "true" : "false";
            }
        }
        catch { /* no systemd-timesyncd */ }
        return d;
    }

    /// <summary>
    /// get_throttled bits: 0 under-voltage now, 2 throttled now,
    /// 16 under-voltage has occurred, 18 throttling has occurred.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseThrottled(string raw)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        var s = raw.Trim();
        var eq = s.IndexOf('=');
        if (eq >= 0) s = s[(eq + 1)..];
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (!long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bits)) return d;
        d["host.underVoltage"] = (bits & ((1L << 0) | (1L << 16))) != 0 ? "true" : "false";
        d["host.throttled"] = (bits & ((1L << 2) | (1L << 18))) != 0 ? "true" : "false";
        return d;
    }
}
