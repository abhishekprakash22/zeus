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
// SettingsFacts — everything a settings check may look at, gathered once:
// the radio state as JSON (camelCase, string enums — the /api/state shape),
// the diagnostic probes' key/values plus host and remote facts (flattened as
// "section.key"), and the recent redacted log. Checks read through these
// helpers and simply skip themselves when a fact is missing, so a check can
// never throw on an older radio, a different board or a non-Linux host.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zeus.Server.Diagnostics;

public sealed class SettingsFacts
{
    public SettingsFacts(JsonNode? state, IReadOnlyDictionary<string, string> extra, IReadOnlyList<string> log)
    {
        State = state;
        Extra = extra;
        Log = log;
    }

    public JsonNode? State { get; }
    public IReadOnlyDictionary<string, string> Extra { get; }
    public IReadOnlyList<string> Log { get; }

    /// <summary>Navigate "a.b[2].c" in the state JSON (null when absent).</summary>
    public JsonNode? Node(string path)
    {
        var cur = State;
        foreach (var raw in path.Split('.'))
        {
            if (cur is null) return null;
            var part = raw;
            int? index = null;
            var lb = part.IndexOf('[');
            if (lb >= 0 && part.EndsWith(']'))
            {
                if (int.TryParse(part[(lb + 1)..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) index = i;
                part = part[..lb];
            }
            if (part.Length > 0)
            {
                if (cur is not JsonObject o || !o.TryGetPropertyValue(part, out var next)) return null;
                cur = next;
            }
            if (index is int idx)
            {
                if (cur is not JsonArray a || idx < 0 || idx >= a.Count) return null;
                cur = a[idx];
            }
        }
        return cur;
    }

    public double? Num(string path) => ToNum(Node(path));

    public bool? Bool(string path)
    {
        var n = Node(path);
        if (n is JsonValue v)
        {
            var k = v.GetValueKind();
            if (k == JsonValueKind.True) return true;
            if (k == JsonValueKind.False) return false;
        }
        return null;
    }

    public string? Str(string path)
    {
        var n = Node(path);
        if (n is null) return null;
        return n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : n.ToJsonString();
    }

    /// <summary>A probe / host / remote fact, e.g. "dsp-audio.audio.muted".</summary>
    public string? X(string key) => Extra.TryGetValue(key, out var v) ? v : null;

    public double? XNum(string key) =>
        double.TryParse(X(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    public bool? XBool(string key) => X(key) switch
    {
        "true" or "True" => true,
        "false" or "False" => false,
        _ => null,
    };

    /// <summary>Index of the newest log line containing <paramref name="needle"/>, or -1.</summary>
    public int LastLog(string needle)
    {
        for (var i = Log.Count - 1; i >= 0; i--)
        {
            if (Log[i].Contains(needle, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    /// <summary>Hardware receivers present and enabled (RX1 always counts).</summary>
    public IReadOnlyList<int> Receivers()
    {
        var list = new List<int>();
        if (Node("receivers") is JsonArray a)
        {
            for (var i = 0; i < a.Count && i < 2; i++)
            {
                var enabled = Bool($"receivers[{i}].enabled");
                if (i == 0 || enabled == true) list.Add(i);
            }
        }
        if (list.Count == 0) list.Add(0);
        return list;
    }

    private static double? ToNum(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.GetValueKind() == JsonValueKind.Number) return v.GetValue<double>();
        if (v.GetValueKind() == JsonValueKind.String &&
            double.TryParse(v.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }
}
