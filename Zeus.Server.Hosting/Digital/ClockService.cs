// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus Digital plugin — clock discipline.
//
// FT8/FT4 slot boundaries MUST come from a monotonic clock disciplined to UTC.
// Using DateTime.UtcNow directly is a real bug source: NTP corrections STEP the
// wall clock, which can jump a station mid-transmission or flip slot parity.
// Here we sample the wall clock once, then advance with Stopwatch (monotonic),
// re-disciplining on a slow cadence.
//
// A Raspberry Pi has no RTC. On a field station with no network the clock can be
// wildly wrong at boot and nothing notices — so we also expose sync health and
// refuse to arm TX when the clock is not trustworthy. A station keying 3 s
// off-slot is simply QRM.

using System.Diagnostics;
using System.Globalization;

namespace Zeus.Server.Hosting.Digital;

public enum ClockSource { None, Manual, TimeSyncd, Chrony, Gps }

public sealed record ClockStatus(
    string Source,
    double OffsetMs,
    double DriftPpm,
    long? SyncedAtUnixMs,
    bool Healthy);

/// <summary>
/// Monotonic UTC clock + host time-sync health.
///
/// <para><see cref="UtcNowMs"/> is what the slot clock must use. It never steps:
/// it is (wall anchor + monotonic elapsed), re-anchored only by
/// <see cref="Discipline"/>, which slews rather than jumps unless the error is
/// gross.</para>
/// </summary>
public sealed class ClockService : IDisposable
{
    /// <summary>Beyond this the clock is not fit to transmit on.</summary>
    public const double MaxHealthyOffsetMs = 1500.0;

    /// <summary>A correction larger than this is applied as a hard step (we were
    /// simply wrong, e.g. first NTP sync after boot); smaller errors are slewed.</summary>
    private const double HardStepThresholdMs = 500.0;

    /// <summary>Fraction of the residual error applied per discipline tick.</summary>
    private const double SlewGain = 0.25;

    private readonly Stopwatch _mono = Stopwatch.StartNew();
    private readonly object _sync = new();
    private readonly Timer _timer;

    private double _anchorUnixMs;      // UTC at _anchorTicks
    private long _anchorTicks;         // _mono.ElapsedTicks at anchor
    private double _offsetMs;          // reported host offset (from chrony/timesyncd)
    private double _driftPpm;
    private ClockSource _source = ClockSource.None;
    private long? _syncedAtUnixMs;

    public ClockService()
    {
        _anchorUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _anchorTicks = _mono.ElapsedTicks;
        Refresh();
        _timer = new Timer(_ => { try { Refresh(); } catch { /* never throw on the timer */ } },
                           null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>Monotonic UTC in unix ms. Use this for ALL slot maths.</summary>
    public double UtcNowMs
    {
        get
        {
            lock (_sync)
            {
                double elapsedMs = (_mono.ElapsedTicks - _anchorTicks) * 1000.0 / Stopwatch.Frequency;
                return _anchorUnixMs + elapsedMs;
            }
        }
    }

    public ClockStatus Status
    {
        get
        {
            lock (_sync)
            {
                bool healthy = _source != ClockSource.None
                               && Math.Abs(_offsetMs) <= MaxHealthyOffsetMs;
                return new ClockStatus(
                    Source: _source switch
                    {
                        ClockSource.Chrony => "chrony",
                        ClockSource.TimeSyncd => "ntp",
                        ClockSource.Gps => "gps",
                        ClockSource.Manual => "manual",
                        _ => "none",
                    },
                    OffsetMs: Math.Round(_offsetMs, 3),
                    DriftPpm: Math.Round(_driftPpm, 3),
                    SyncedAtUnixMs: _syncedAtUnixMs,
                    Healthy: healthy);
            }
        }
    }

    /// <summary>True when it is safe to key. TX arming must consult this.</summary>
    public bool SafeToTransmit => Status.Healthy;

    /// <summary>
    /// Re-anchor against the (possibly just-corrected) wall clock. Small errors
    /// are slewed so slot boundaries stay continuous; a gross error is stepped.
    /// </summary>
    private void Discipline()
    {
        lock (_sync)
        {
            double wall = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            double elapsedMs = (_mono.ElapsedTicks - _anchorTicks) * 1000.0 / Stopwatch.Frequency;
            double mine = _anchorUnixMs + elapsedMs;
            double err = wall - mine;

            if (Math.Abs(err) >= HardStepThresholdMs)
            {
                _anchorUnixMs = wall;                     // step
                _anchorTicks = _mono.ElapsedTicks;
            }
            else
            {
                _anchorUnixMs += err * SlewGain;          // slew
            }
        }
    }

    /// <summary>Poll the host's time-sync daemon. Cheap; Pi OS ships one of these.</summary>
    private void Refresh()
    {
        Discipline();

        if (TryChrony(out double off, out double drift))
        {
            lock (_sync)
            {
                _source = ClockSource.Chrony;
                _offsetMs = off;
                _driftPpm = drift;
                _syncedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            return;
        }

        if (TryTimedatectl(out bool synced))
        {
            lock (_sync)
            {
                _source = synced ? ClockSource.TimeSyncd : ClockSource.None;
                // timesyncd exposes no offset; assume in-spec when it claims sync.
                _offsetMs = synced ? 0.0 : double.NaN;
                if (synced) _syncedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            return;
        }

        lock (_sync) { _source = ClockSource.None; _offsetMs = double.NaN; }
    }

    /// <summary>`chronyc tracking` → System time offset + frequency drift.</summary>
    private static bool TryChrony(out double offsetMs, out double driftPpm)
    {
        offsetMs = 0; driftPpm = 0;
        if (!TryRun("chronyc", "-n tracking", out string stdout)) return false;

        bool gotOffset = false;
        foreach (var line in stdout.Split('\n'))
        {
            // "System time     : 0.000123456 seconds fast of NTP time"
            if (line.StartsWith("System time", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(':', 2);
                if (parts.Length < 2) continue;
                var tok = parts[1].Trim().Split(' ');
                if (tok.Length > 0 && double.TryParse(tok[0], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double secs))
                {
                    bool slow = parts[1].Contains("slow", StringComparison.OrdinalIgnoreCase);
                    offsetMs = (slow ? -secs : secs) * 1000.0;
                    gotOffset = true;
                }
            }
            // "Frequency       : 12.345 ppm fast"
            else if (line.StartsWith("Frequency", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(':', 2);
                if (parts.Length < 2) continue;
                var tok = parts[1].Trim().Split(' ');
                if (tok.Length > 0 && double.TryParse(tok[0], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double ppm))
                    driftPpm = ppm;
            }
        }
        return gotOffset;
    }

    /// <summary>`timedatectl show -p NTPSynchronized --value` → yes/no.</summary>
    private static bool TryTimedatectl(out bool synced)
    {
        synced = false;
        if (!TryRun("timedatectl", "show -p NTPSynchronized --value", out string stdout))
            return false;
        synced = stdout.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static bool TryRun(string file, string args, out string stdout)
    {
        stdout = "";
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return false;
            stdout = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(2000)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;   // not installed / not permitted — caller falls through
        }
    }

    public void Dispose() => _timer.Dispose();
}
