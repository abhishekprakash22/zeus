// SPDX-License-Identifier: GPL-2.0-or-later
//
// InstanceGuard — exactly one Zeus may own the HTTP port.
//
// Field incident 2026-09-10: two radios were running on one Pi. A self-update
// handoff had launched the new build from a detached shell, outside the
// systemd unit; the old main process exited 0, systemd marked the unit
// inactive, and the real radio lived on as an orphan. The next
// `systemctl --user restart zeus` started a SECOND radio. Both claimed the
// same remote-access room with the same host key, both answered broker
// offers, both wrote interleaved lines into one log — and the stale one kept
// serving old code after every fix had shipped. A day of contradictory
// evidence traced back to that one twin.
//
// This guard runs before Kestrel binds. It finds the listening socket on the
// HTTP port through /proc/net/tcp*, maps the socket inode to its owning pid,
// and — if that owner is a Zeus process — terminates it and its whole tree
// (outer AppImage runtime, inner binary, kiosk browser, its p2app child),
// then waits for the port to free. A non-Zeus owner is left alone and logged;
// the bind will fail exactly as it does today, which is the correct outcome
// for foreign software squatting on our port.
//
// Linux only: the /proc walk has no portable equivalent, and the twin problem
// only exists on the systemd-supervised appliance.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Zeus.Server;

public static class InstanceGuard
{
    private const int SIGTERM = 15;
    private const int SIGKILL = 9;

    [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static extern int sys_kill(int pid, int sig);

    /// <summary>
    /// Make this process the sole owner of <paramref name="httpPort"/>: if a stale
    /// Zeus twin is listening there, terminate it (and its process tree) and wait
    /// for the port to free. Never throws — a guard failure must not stop startup.
    /// </summary>
    public static void EnsureSoleOwner(int httpPort, ILogger log)
    {
        if (!OperatingSystem.IsLinux() || httpPort <= 0) return;

        try
        {
            var owner = FindListenerPid(httpPort);
            if (owner is null)
            {
                log.LogInformation("instance guard: :{Port} is free — sole owner", httpPort);
                return;
            }

            int pid = owner.Value;
            string cmd = ReadCmdline(pid);

            if (!IsZeusProcess(cmd))
            {
                log.LogWarning(
                    "instance guard: :{Port} is owned by non-Zeus pid {Pid} ({Cmd}) — leaving it alone; our bind will fail",
                    httpPort, pid, cmd);
                return;
            }

            if (IsSelfOrAncestor(pid))
            {
                // Our own AppImage runtime can't be listening, but never kill up
                // our own parent chain under any reading of /proc.
                log.LogInformation("instance guard: :{Port} owner pid {Pid} is in our own process tree — nothing to do", httpPort, pid);
                return;
            }

            var victims = CollectTree(pid);
            log.LogWarning(
                "instance guard: terminating stale twin pid {Pid} ({Cmd}, started {Started}) squatting on :{Port} — {N} process(es) in its tree",
                pid, cmd, StartTime(pid), httpPort, victims.Count);

            foreach (var v in victims) TrySignal(v, SIGTERM);

            if (!WaitForPortFree(httpPort, TimeSpan.FromSeconds(5)))
            {
                log.LogWarning("instance guard: twin ignored SIGTERM — escalating to SIGKILL");
                foreach (var v in victims) TrySignal(v, SIGKILL);
                WaitForPortFree(httpPort, TimeSpan.FromSeconds(3));
            }

            if (FindListenerPid(httpPort) is null)
                log.LogInformation("instance guard: :{Port} reclaimed — twin pid {Pid} gone", httpPort, pid);
            else
                log.LogError("instance guard: :{Port} STILL held after termination attempts — bind will fail", httpPort);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "instance guard: probe failed; continuing without it");
        }
    }

    // ---- /proc plumbing ------------------------------------------------------

    /// <summary>Pid of the process holding a LISTEN socket on <paramref name="port"/>, excluding ourselves; null if none.</summary>
    internal static int? FindListenerPid(int port)
    {
        var inodes = ListenInodes(port);
        if (inodes.Count == 0) return null;

        int self = Environment.ProcessId;
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out int pid) || pid == self) continue;
            string fdDir = Path.Combine(dir, "fd");
            if (!Directory.Exists(fdDir)) continue;
            IEnumerable<string> fds;
            try { fds = Directory.EnumerateFiles(fdDir); }
            catch { continue; } // not ours to read
            foreach (var fd in fds)
            {
                string? target;
                try { target = new FileInfo(fd).LinkTarget; }
                catch { continue; }
                if (target is null || !target.StartsWith("socket:[", StringComparison.Ordinal)) continue;
                var inode = target.AsSpan(8, target.Length - 9);
                if (inodes.Contains(inode.ToString())) return pid;
            }
        }
        return null;
    }

    /// <summary>Socket inodes in LISTEN state on <paramref name="port"/> from /proc/net/tcp and tcp6.</summary>
    private static HashSet<string> ListenInodes(int port)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        string hexPort = port.ToString("X4");
        foreach (var table in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            if (!File.Exists(table)) continue;
            foreach (var line in File.ReadLines(table).Skip(1))
            {
                // sl local_address rem_address st ... uid timeout inode
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 10) continue;
                if (f[3] != "0A") continue;                         // LISTEN
                int colon = f[1].LastIndexOf(':');
                if (colon < 0 || !f[1].AsSpan(colon + 1).Equals(hexPort, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(f[9]);
            }
        }
        return result;
    }

    internal static string ReadCmdline(int pid)
    {
        try
        {
            var raw = File.ReadAllText($"/proc/{pid}/cmdline");
            return raw.Replace('\0', ' ').Trim();
        }
        catch { return ""; }
    }

    internal static bool IsZeusProcess(string cmdline) =>
        cmdline.Contains("OpenhpsdrZeus", StringComparison.Ordinal);

    private static int ParentPid(int pid)
    {
        try
        {
            // "pid (comm) state ppid ..." — comm may contain spaces/parens, so split after the last ')'.
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            int close = stat.LastIndexOf(')');
            var rest = stat.AsSpan(close + 2).ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return rest.Length >= 2 && int.TryParse(rest[1], out int ppid) ? ppid : 0;
        }
        catch { return 0; }
    }

    private static bool IsSelfOrAncestor(int pid)
    {
        int cur = Environment.ProcessId;
        for (int hops = 0; hops < 64 && cur > 1; hops++)
        {
            if (cur == pid) return true;
            cur = ParentPid(cur);
        }
        return false;
    }

    /// <summary>
    /// The twin's whole tree: its Zeus ancestors (the outer AppImage runtime that
    /// holds the FUSE mount), itself, and every descendant (kiosk browser, p2app).
    /// Ordered leaves-first so children die before the parents that would reap them.
    /// </summary>
    private static List<int> CollectTree(int pid)
    {
        // Parent map for the whole system, one pass.
        var parentOf = new Dictionary<int, int>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
            if (int.TryParse(Path.GetFileName(dir), out int p))
                parentOf[p] = ParentPid(p);

        var set = new HashSet<int> { pid };

        // Walk UP while the ancestor is still a Zeus process (outer AppImage runtime).
        int up = pid;
        for (int hops = 0; hops < 8; hops++)
        {
            parentOf.TryGetValue(up, out int par);
            if (par <= 1 || !IsZeusProcess(ReadCmdline(par))) break;
            set.Add(par);
            up = par;
        }

        // Walk DOWN: every process whose ancestor chain hits the set.
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var (child, par) in parentOf)
                if (!set.Contains(child) && set.Contains(par) && child != Environment.ProcessId)
                {
                    set.Add(child);
                    grew = true;
                }
        }

        set.Remove(Environment.ProcessId);
        // Leaves first: deeper pids (children) before parents — approximate by
        // sorting so descendants of a pid come before it.
        var ordered = set.ToList();
        ordered.Sort((a, b) => Depth(b, parentOf).CompareTo(Depth(a, parentOf)));
        return ordered;
    }

    private static int Depth(int pid, Dictionary<int, int> parentOf)
    {
        int d = 0;
        for (int cur = pid; cur > 1 && d < 64; d++)
        {
            if (!parentOf.TryGetValue(cur, out cur)) break;
        }
        return d;
    }

    private static void TrySignal(int pid, int sig)
    {
        try { sys_kill(pid, sig); } catch { /* already gone or not permitted */ }
    }

    private static bool WaitForPortFree(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (FindListenerPid(port) is null) return true;
            Thread.Sleep(100);
        }
        return FindListenerPid(port) is null;
    }

    private static string StartTime(int pid)
    {
        try { return Process.GetProcessById(pid).StartTime.ToString("yyyy-MM-dd HH:mm:ss"); }
        catch { return "unknown"; }
    }
}
