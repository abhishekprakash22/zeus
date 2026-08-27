// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// Ganymede / G2-1K amplifier protection controller integration.
//
// The G2-1K's protection controller ("Ganymede", Laurence Barker G8NJJ —
// https://github.com/laurencebarker/Ganymede) trips the amplifier in
// hardware (bias + PTT dropped in ~10 µs) and REPORTS the event over an
// ANDROMEDA-style CAT serial link. In the Thetis world p2app relays those
// messages; Zeus owns the radio directly, so this service owns the serial
// link instead, mirroring G2FrontPanelService's port discipline.
//
// Protocol (ganymede design notes, "CAT" + message tables):
//   ZZZS;          → identity request. Reply ZZZSppnnmmm; pp=03 is Ganymede
//                    (nn = hardware version, mmm = software version).
//   ZZZAnn;        ← trip status, nn is a BITMASK:
//                      0 amplifier OK        1 excessive reverse power
//                      2 excessive drain amps 4 PSU voltage out of spec
//                      8 heatsink over-temp   16 excessive forward power
//                      64 was tripped — awaiting reset
//   ZZZA32;        → reset request (no reply on success; a fresh trip
//                    report if the fault immediately recurs).
//
// Duties here: keep the link up, decode ZZZA into human words, force MOX
// off through the SAME idempotent path the SWR trip uses (TryTripForAlert
// → AlertFrame broadcast, so LAN + remote clients all see the banner),
// answer /api/amp/status, and write ZZZA32; on operator RESET. Trips are
// enforced by Ganymede's hardware regardless — Zeus reacting is about
// stopping drive into a dead PA and telling the operator WHY.

using System.IO.Ports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;

namespace Zeus.Server.Hosting.Amp;

public sealed class GanymedeAmpService : BackgroundService
{
    private const int Baud = 9600; // USB CDC — rate nominal, matches ANDROMEDA links
    private static readonly TimeSpan ProbeReplyWait = TimeSpan.FromMilliseconds(1800);
    private static readonly TimeSpan RescanDelay = TimeSpan.FromSeconds(10);

    private readonly TxService _tx;
    private readonly ILogger<GanymedeAmpService> _log;
    private readonly object _sync = new();

    private SerialPort? _port;
    private string? _activePath;
    private volatile bool _connected;
    private string? _version;
    private int _tripMask;
    private DateTime? _lastTripUtc;

    public GanymedeAmpService(TxService tx, ILogger<GanymedeAmpService> log)
    {
        _tx = tx;
        _log = log;
    }

    public object StatusSnapshot()
    {
        lock (_sync)
        {
            return new
            {
                connected = _connected,
                port = _activePath,
                version = _version,
                tripMask = _tripMask,
                tripLabels = DescribeTrip(_tripMask),
                awaitingReset = (_tripMask & 64) != 0,
                lastTripUtc = _lastTripUtc,
            };
        }
    }

    /// <summary>Operator reset (ZZZA32;). False when no controller is connected.</summary>
    public bool RequestReset()
    {
        var port = _port;
        if (port is null || !_connected) return false;
        try
        {
            port.Write("ZZZA32;");
            _log.LogInformation("ganymede.reset requested by operator");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ganymede.reset write failed");
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Let G2FrontPanelService adopt its udev-symlinked panel port first so
        // a probe here can never race it for the same underlying tty.
        try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch { return; }

        while (!ct.IsCancellationRequested)
        {
            string? adopted = null;
            try
            {
                adopted = await FindControllerAsync(ct);
                if (adopted is null)
                {
                    await Task.Delay(RescanDelay, ct);
                    continue;
                }
                await RunSessionAsync(adopted, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "ganymede.session.error device={Dev}; rescanning", adopted);
                await SafeDelay(TimeSpan.FromSeconds(5), ct);
            }
            finally
            {
                ClosePort();
            }
        }
    }

    private async Task<string?> FindControllerAsync(CancellationToken ct)
    {
        foreach (var path in CandidatePorts())
        {
            ct.ThrowIfCancellationRequested();
            SerialPort? probe = null;
            try
            {
                probe = OpenPort(path);
                // Arduino-class boards reset on open; wait out the bootloader
                // so the identity request isn't swallowed.
                await Task.Delay(TimeSpan.FromMilliseconds(700), ct);
                probe.Write("ZZZS;");
                var reply = await ReadUntilSemicolonAsync(probe, ProbeReplyWait, ct);
                if (reply is not null && reply.StartsWith("ZZZS03", StringComparison.Ordinal))
                {
                    _port = probe;
                    lock (_sync)
                    {
                        _activePath = path;
                        _version = reply.Length > 6 ? reply[6..] : "";
                        _connected = true;
                    }
                    _log.LogInformation("ganymede.open device={Dev} version={Ver}", path, _version);
                    return path;
                }
                probe.Dispose(); // some other CAT device (panel replies ZZZS with a different id) — leave it be
            }
            catch (OperationCanceledException) { probe?.Dispose(); throw; }
            catch
            {
                // busy (the front panel holds it), vanished, or not a serial
                // device we can speak to — all mean "not ours", move on.
                try { probe?.Dispose(); } catch { /* already gone */ }
            }
        }
        return null;
    }

    private static async Task<string?> ReadUntilSemicolonAsync(SerialPort port, TimeSpan budget, CancellationToken ct)
    {
        var acc = new System.Text.StringBuilder(32);
        var buf = new byte[64];
        var deadline = DateTime.UtcNow + budget;
        var stream = port.BaseStream;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var slice = CancellationTokenSource.CreateLinkedTokenSource(ct);
            slice.CancelAfter(250);
            int n;
            try
            {
                n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), slice.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                continue; // slice timeout — poll again inside the budget
            }
            if (n <= 0) continue;
            for (int i = 0; i < n; i++)
            {
                char c = (char)buf[i];
                if (c == ';') return acc.ToString();
                if (!char.IsControl(c) && acc.Length < 31) acc.Append(c);
            }
        }
        return null;
    }

    private static IEnumerable<string> CandidatePorts()
    {
        var explicitPath = Environment.GetEnvironmentVariable("ZEUS_GANYMEDE_PORT")?.Trim();
        if (!string.IsNullOrEmpty(explicitPath))
        {
            yield return explicitPath;
            yield break; // explicit config disables scanning entirely
        }
        if (OperatingSystem.IsLinux())
        {
            // udev symlink first (mirrors the front panel's convention)
            if (File.Exists("/dev/ganymede")) yield return "/dev/ganymede";
            for (int i = 0; i < 8; i++)
            {
                var p = $"/dev/ttyACM{i}";
                if (File.Exists(p)) yield return p;
            }
            for (int i = 0; i < 8; i++)
            {
                var p = $"/dev/ttyUSB{i}";
                if (File.Exists(p)) yield return p;
            }
        }
        else
        {
            foreach (var p in SerialPort.GetPortNames()) yield return p;
        }
    }

    private static SerialPort OpenPort(string path)
    {
        var port = new SerialPort(path, Baud, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = 250,
            WriteTimeout = 500,
        };
        port.Open();
        return port;
    }

    private async Task RunSessionAsync(string path, CancellationToken ct)
    {
        var port = _port!;
        var buf = new byte[256];
        var acc = new System.Text.StringBuilder(64);
        var stream = port.BaseStream;
        while (!ct.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogInformation(ex, "ganymede.link lost device={Dev}", path);
                break;
            }
            if (n <= 0) break;
            for (int i = 0; i < n; i++)
            {
                char c = (char)buf[i];
                if (c == ';')
                {
                    HandleMessage(acc.ToString());
                    acc.Clear();
                }
                else if (!char.IsControl(c) && acc.Length < 63)
                {
                    acc.Append(c);
                }
            }
        }
    }

    private void HandleMessage(string msg)
    {
        if (msg.StartsWith("ZZZS03", StringComparison.Ordinal))
        {
            lock (_sync) _version = msg.Length > 6 ? msg[6..] : "";
            return;
        }
        if (!msg.StartsWith("ZZZA", StringComparison.Ordinal)) return;
        if (!int.TryParse(msg.AsSpan(4), out int mask)) return;

        bool newTrip;
        lock (_sync)
        {
            newTrip = mask != 0 && mask != _tripMask;
            _tripMask = mask;
            if (mask != 0) _lastTripUtc = DateTime.UtcNow;
        }

        if (mask == 0)
        {
            _log.LogInformation("ganymede.clear — amplifier OK");
            return;
        }

        var words = string.Join(", ", DescribeTrip(mask));
        _log.LogWarning("ganymede.trip mask={Mask} ({Words})", mask, words);
        if (newTrip)
        {
            // Same idempotent path as the SWR trip: MOX/TUN forced off, the
            // AlertFrame rides to every client (LAN and remote alike).
            _tx.TryTripForAlert(AlertKind.AmpTrip,
                $"Amplifier tripped \u2014 {words}. Drive removed; press RESET AMP when the fault is cleared.");
        }
    }

    internal static string[] DescribeTrip(int mask)
    {
        var list = new List<string>(3);
        if ((mask & 1) != 0) list.Add("excessive reverse power (check antenna/LPF)");
        if ((mask & 2) != 0) list.Add("excessive drain current");
        if ((mask & 4) != 0) list.Add("PSU voltage out of spec");
        if ((mask & 8) != 0) list.Add("heatsink over-temperature");
        if ((mask & 16) != 0) list.Add("excessive forward power");
        if ((mask & 64) != 0) list.Add("awaiting reset");
        return list.ToArray();
    }

    private async Task SafeDelay(TimeSpan t, CancellationToken ct)
    {
        try { await Task.Delay(t, ct); } catch { /* shutdown */ }
    }

    private void ClosePort()
    {
        var p = _port;
        _port = null;
        lock (_sync)
        {
            _connected = false;
            _activePath = null;
        }
        try { p?.Dispose(); } catch { /* already down */ }
    }
}
