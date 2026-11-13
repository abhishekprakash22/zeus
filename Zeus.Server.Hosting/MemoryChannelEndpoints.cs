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
// Memory channels: frequency + mode (+ filter) the operator stored, most often
// from the spectrum radial menu. Kept radio-side as a JSON file beside the
// prefs database so every client (kiosk, desktop browser, remote) shares one
// list and the kiosk's throwaway browser profile can't lose it.

using System.Text.Json;

namespace Zeus.Server;

public sealed record MemoryChannel(
    string? Id,
    string? Name,
    long Hz,
    string? Mode,
    int? FilterLowHz,
    int? FilterHighHz,
    long CreatedUnixMs);

public static class MemoryChannelEndpoints
{
    internal const int MaxChannels = 200;
    internal const int MaxNameLength = 24;

    private static readonly object FileLock = new();

    private static readonly HashSet<string> Modes = new(StringComparer.Ordinal)
    {
        "LSB", "USB", "CWL", "CWU", "AM", "FM", "SAM", "DSB", "DIGL", "DIGU", "FREEDV",
    };

    private static string StorePath()
    {
        var dir = Path.GetDirectoryName(PrefsDbPath.Get()) ?? ".";
        return Path.Combine(dir, "memory-channels.json");
    }

    /// <summary>Validate one channel; null when it is junk. Assigns an id and a
    /// default name when missing, trims the name, and drops an inverted or
    /// absurd filter rather than the whole channel.</summary>
    internal static MemoryChannel? Normalize(MemoryChannel? m, long nowUnixMs)
    {
        if (m is null) return null;
        if (m.Hz < 10_000 || m.Hz > 10_000_000_000) return null;
        var mode = (m.Mode ?? "").Trim().ToUpperInvariant();
        if (!Modes.Contains(mode)) return null;
        var id = string.IsNullOrWhiteSpace(m.Id) || m.Id.Length > 40
            ? Guid.NewGuid().ToString("N")[..12]
            : m.Id.Trim();
        var name = new string((m.Name ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (name.Length > MaxNameLength) name = name[..MaxNameLength].TrimEnd();
        if (name.Length == 0)
            name = (m.Hz / 1e6).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " " + mode;
        int? lo = m.FilterLowHz, hi = m.FilterHighHz;
        if (lo is null || hi is null || lo >= hi || Math.Abs(lo.Value) > 20_000 || Math.Abs(hi.Value) > 20_000)
        {
            lo = null;
            hi = null;
        }
        var created = m.CreatedUnixMs > 0 ? m.CreatedUnixMs : nowUnixMs;
        return new MemoryChannel(id, name, m.Hz, mode, lo, hi, created);
    }

    /// <summary>Normalise a whole list: junk dropped, duplicate ids keep the
    /// first, capped at <see cref="MaxChannels"/>, sorted by frequency.</summary>
    internal static List<MemoryChannel> NormalizeList(IEnumerable<MemoryChannel?>? list, long nowUnixMs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var outList = new List<MemoryChannel>();
        if (list is not null)
        {
            foreach (var raw in list)
            {
                if (outList.Count >= MaxChannels) break;
                var m = Normalize(raw, nowUnixMs);
                if (m?.Id is null || !seen.Add(m.Id)) continue;
                outList.Add(m);
            }
        }
        outList.Sort((a, b) => a.Hz.CompareTo(b.Hz));
        return outList;
    }

    /// <summary>Add a channel. A channel within 10 Hz of an existing one in the
    /// same mode replaces it (re-storing a frequency renames, it doesn't stack
    /// duplicates). The oldest channel is dropped when the list is full.</summary>
    internal static List<MemoryChannel> Add(List<MemoryChannel> list, MemoryChannel? channel, long nowUnixMs)
    {
        var m = Normalize(channel, nowUnixMs);
        if (m is null) return list;
        var next = list
            .Where(x => x.Id != m.Id && !(Math.Abs(x.Hz - m.Hz) <= 10 && x.Mode == m.Mode))
            .ToList();
        while (next.Count >= MaxChannels)
        {
            var oldest = next.MinBy(x => x.CreatedUnixMs);
            if (oldest is null) break;
            next.Remove(oldest);
        }
        next.Add(m);
        next.Sort((a, b) => a.Hz.CompareTo(b.Hz));
        return next;
    }

    private static List<MemoryChannel> Load()
    {
        try
        {
            var path = StorePath();
            if (!File.Exists(path)) return new List<MemoryChannel>();
            var raw = JsonSerializer.Deserialize<List<MemoryChannel?>>(File.ReadAllText(path));
            return NormalizeList(raw, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        catch
        {
            return new List<MemoryChannel>();
        }
    }

    private static void Save(List<MemoryChannel> list)
    {
        try
        {
            var path = StorePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Read-only data dir etc. — storage is best-effort; the response
            // still carries the list the operator just built.
        }
    }

    public static IEndpointRouteBuilder MapMemoryChannelEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/memories", () =>
        {
            lock (FileLock) return Results.Ok(Load());
        });

        app.MapPost("/api/memories", (MemoryChannel req) =>
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (Normalize(req, now) is null)
                return Results.BadRequest(new { error = "invalid memory channel" });
            lock (FileLock)
            {
                var next = Add(Load(), req, now);
                Save(next);
                return Results.Ok(next);
            }
        });

        app.MapDelete("/api/memories/{id}", (string id) =>
        {
            lock (FileLock)
            {
                var list = Load();
                var next = list.Where(x => x.Id != id).ToList();
                if (next.Count != list.Count) Save(next);
                return Results.Ok(next);
            }
        });

        return app;
    }
}
