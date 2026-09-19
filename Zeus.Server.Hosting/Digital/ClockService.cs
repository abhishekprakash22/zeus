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

public enum ClockSource { None, Manual, TimeSyncd, Chrony, Gps, Sntp }

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
                        ClockSource.Sntp => "sntp",
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

    /// <summary>
    /// Poll the host's time-sync daemon (chrony / timesyncd — Pi OS ships one of
    /// these); elsewhere, measure the offset with an occasional SNTP query.
    /// </summary>
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

        if (TryTimedatectl(out bool synced) && synced)
        {
            lock (_sync)
            {
                _source = ClockSource.TimeSyncd;
                // timesyncd exposes no offset; assume in-spec when it claims sync.
                _offsetMs = 0.0;
                _syncedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            return;
        }

        // No chrony, and timedatectl absent (macOS, Windows) or not yet synced:
        // measure the offset ourselves with one SNTP query. Without this every
        // non-Linux host reported "none" and FT8 TX could never arm.
        if (TrySntpCached(out double sntpOffsetMs))
        {
            lock (_sync)
            {
                _source = ClockSource.Sntp;
                _offsetMs = sntpOffsetMs;
                _syncedAtUnixMs = _sntpAtUnixMs;
            }
            return;
        }

        lock (_sync) { _source = ClockSource.None; _offsetMs = double.NaN; }
    }

    // ---- SNTP fallback ------------------------------------------------------

    // NTP pool etiquette: no more than one query per few minutes from a client.
    private static readonly TimeSpan SntpInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SntpRetryAfterFailure = TimeSpan.FromMinutes(1);
    private const int SntpTimeoutMs = 1500;
    private double _sntpOffsetMs = double.NaN;
    private long? _sntpAtUnixMs;
    private long _sntpNextAttemptUnixMs;

    /// <summary>The platform's own time server — what the OS itself syncs to by default.</summary>
    private static string SntpServer() =>
        OperatingSystem.IsMacOS() ? "time.apple.com"
        : OperatingSystem.IsWindows() ? "time.windows.com"
        : "pool.ntp.org";

    private bool TrySntpCached(out double offsetMs)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now < _sntpNextAttemptUnixMs)
        {
            offsetMs = _sntpOffsetMs;
            return !double.IsNaN(offsetMs);
        }
        if (TrySntp(SntpServer(), out offsetMs))
        {
            _sntpOffsetMs = offsetMs;
            _sntpAtUnixMs = now;
            _sntpNextAttemptUnixMs = now + (long)SntpInterval.TotalMilliseconds;
            return true;
        }
        _sntpOffsetMs = double.NaN;
        _sntpNextAttemptUnixMs = now + (long)SntpRetryAfterFailure.TotalMilliseconds;
        return false;
    }

    /// <summary>
    /// One SNTP (RFC 4330) exchange: offset = ((T2 − T1) + (T3 − T4)) / 2, where
    /// T1/T4 are our send/receive times and T2/T3 the server's receive/transmit
    /// timestamps. Positive means the local clock is behind the server.
    /// </summary>
    internal static bool TrySntp(string server, out double offsetMs)
    {
        offsetMs = double.NaN;
        try
        {
            using var udp = new System.Net.Sockets.UdpClient();
            udp.Client.ReceiveTimeout = SntpTimeoutMs;
            udp.Client.SendTimeout = SntpTimeoutMs;
            udp.Connect(server, 123);

            var request = new byte[48];
            request[0] = 0x1B;                        // LI 0, VN 3, mode 3 (client)
            double t1 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            udp.Send(request, request.Length);
            var remote = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
            var reply = udp.Receive(ref remote);
            double t4 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return TryComputeSntpOffset(reply, t1, t4, out offsetMs);
        }
        catch
        {
            return false;   // no network / blocked port / DNS — caller reports "none"
        }
    }

    /// <summary>Offset from a 48-byte SNTP reply and local send/receive unix-ms times.</summary>
    internal static bool TryComputeSntpOffset(byte[] reply, double t1, double t4, out double offsetMs)
    {
        offsetMs = double.NaN;
        if (reply.Length < 48) return false;
        int mode = reply[0] & 0x7;
        int stratum = reply[1];
        if (mode != 4 || stratum == 0 || stratum > 15) return false;   // not a valid server reply / KoD
        double t2 = NtpTimestampToUnixMs(reply, 32);
        double t3 = NtpTimestampToUnixMs(reply, 40);
        if (t2 <= 0 || t3 <= 0) return false;
        offsetMs = ((t2 - t1) + (t3 - t4)) / 2.0;
        return true;
    }

    private static double NtpTimestampToUnixMs(byte[] b, int at)
    {
        const double NtpToUnixSeconds = 2_208_988_800.0;   // 1900-01-01 → 1970-01-01
        uint secs = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at, 4));
        uint frac = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at + 4, 4));
        if (secs == 0) return 0;
        return (secs - NtpToUnixSeconds) * 1000.0 + frac * 1000.0 / 4294967296.0;
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
