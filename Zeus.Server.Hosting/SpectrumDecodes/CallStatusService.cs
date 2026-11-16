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
// Logbook status for callsigns painted on the spectrum (FT8 decodes, CW
// skimmer calls, cluster spots): for a call heard on a frequency, is it a new
// DXCC entity, an entity new on this band, a call not yet worked on this band
// ("needed"), or already worked here? The log is indexed once (calls and
// entities per band, any mode) and re-indexed when its entry count changes,
// checked at most every few seconds, so a fresh QSO recolours within moments.

using Zeus.Plugins.Contracts.Extensions;
using Zeus.Server.Hosting.Dxcc;

namespace Zeus.Server;

public enum SpectrumCallStatus
{
    /// <summary>The call's DXCC entity is not in the log on any band.</summary>
    NewDxcc,
    /// <summary>The entity is in the log, but not on this band.</summary>
    NewBand,
    /// <summary>The call has not been worked on this band.</summary>
    Needed,
    /// <summary>The call has been worked on this band.</summary>
    Worked,
}

public sealed record CallStatusItem(string? Call, long FreqHz);

public sealed record CallStatusRequest(IReadOnlyList<CallStatusItem>? Items);

public sealed record CallStatusResult(
    string Call,
    long FreqHz,
    string? Band,
    int? Dxcc,
    string? Entity,
    string? Status,
    bool WorkedAnyBand);

/// <summary>Calls and DXCC entities in the log, each with the bands worked.</summary>
public sealed class CallStatusIndex
{
    public static readonly CallStatusIndex Empty = new(
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal),
        new Dictionary<int, HashSet<string>>());

    public CallStatusIndex(
        IReadOnlyDictionary<string, HashSet<string>> callBands,
        IReadOnlyDictionary<int, HashSet<string>> dxccBands)
    {
        CallBands = callBands;
        DxccBands = dxccBands;
    }

    public IReadOnlyDictionary<string, HashSet<string>> CallBands { get; }
    public IReadOnlyDictionary<int, HashSet<string>> DxccBands { get; }
}

public sealed class CallStatusService : IDisposable
{
    internal const int MaxItems = 200;
    private const int PageSize = 500;
    private static readonly TimeSpan CountCheckInterval = TimeSpan.FromSeconds(5);

    private readonly LogbookPluginBridge _logbook;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CallStatusIndex _index = CallStatusIndex.Empty;
    private ILogbookPlugin? _indexedFrom;
    private int _indexedCount = -1;
    private DateTimeOffset _lastCountCheck = DateTimeOffset.MinValue;

    public CallStatusService(LogbookPluginBridge logbook, TimeProvider? time = null)
    {
        _logbook = logbook;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The bare callsign used for matching: upper case, without
    /// portable suffixes (/P /M /QRP …) but keeping a prefix (VK2/G4ABC
    /// stays, its entity is VK).</summary>
    internal static string NormalizeCall(string? call)
    {
        var c = (call ?? "").Trim().Trim('<', '>').ToUpperInvariant();
        var slash = c.LastIndexOf('/');
        if (slash > 0)
        {
            var suffix = c[(slash + 1)..];
            if (suffix is "P" or "M" or "MM" or "AM" or "QRP" or "A" || (suffix.Length == 1 && char.IsDigit(suffix[0])))
                c = c[..slash];
        }
        return c;
    }

    internal static string? NormalizeBand(string? band)
    {
        var b = (band ?? "").Trim().ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal);
        if (b.Length == 0) return null;
        // Bare numbers ("20") are metres.
        return b.All(char.IsDigit) ? b + "m" : b;
    }

    /// <summary>Index a set of log entries. Pure, for tests.</summary>
    internal static CallStatusIndex BuildIndex(IEnumerable<LogbookEntrySnapshot> entries, DxccTable dxcc)
    {
        var calls = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var entities = new Dictionary<int, HashSet<string>>();
        foreach (var e in entries)
        {
            var call = NormalizeCall(e.Callsign);
            if (call.Length == 0) continue;
            var band = NormalizeBand(e.Band)
                ?? (e.FrequencyMhz is double mhz ? NormalizeBand(BandUtils.FreqToBand((long)Math.Round(mhz * 1e6))) : null)
                ?? "";
            if (!calls.TryGetValue(call, out var cb)) calls[call] = cb = new HashSet<string>(StringComparer.Ordinal);
            cb.Add(band);
            var d = e.Dxcc ?? dxcc.Lookup(e.Callsign)?.Dxcc;
            if (d is int id)
            {
                if (!entities.TryGetValue(id, out var db)) entities[id] = db = new HashSet<string>(StringComparer.Ordinal);
                db.Add(band);
            }
        }
        return new CallStatusIndex(calls, entities);
    }

    /// <summary>Status of one call heard on one frequency. Pure, for tests.
    /// Status is null when there is no logbook to judge against.</summary>
    internal static CallStatusResult Classify(
        CallStatusIndex? index, string call, long freqHz, DxccTable dxcc)
    {
        // Answer under the call as asked (the client keys its cache by it);
        // match on the bare call.
        var asked = (call ?? "").Trim().ToUpperInvariant();
        var norm = NormalizeCall(call);
        var band = NormalizeBand(BandUtils.FreqToBand(freqHz));
        var entity = dxcc.Lookup(norm.Length > 0 ? norm : call);
        bool workedAny = index is not null && index.CallBands.ContainsKey(norm);
        if (index is null)
            return new CallStatusResult(asked, freqHz, band, entity?.Dxcc, entity?.Name, null, false);

        string status;
        if (index.CallBands.TryGetValue(norm, out var callBands) && band is not null && callBands.Contains(band))
            status = nameof(SpectrumCallStatus.Worked);
        else if (entity is not null && !index.DxccBands.ContainsKey(entity.Dxcc))
            status = nameof(SpectrumCallStatus.NewDxcc);
        else if (entity is not null && band is not null && !index.DxccBands[entity.Dxcc].Contains(band))
            status = nameof(SpectrumCallStatus.NewBand);
        else
            status = nameof(SpectrumCallStatus.Needed);
        return new CallStatusResult(asked, freqHz, band, entity?.Dxcc, entity?.Name, status, workedAny);
    }

    /// <summary>Classify a batch against the live log. Calls that don't look
    /// like callsigns are dropped; at most <see cref="MaxItems"/> are answered.</summary>
    public async Task<IReadOnlyList<CallStatusResult>> ClassifyAsync(
        IEnumerable<CallStatusItem>? items, CancellationToken ct)
    {
        var list = (items ?? Array.Empty<CallStatusItem>())
            .Where(i => i is not null && LooksLikeCall(i.Call) && i.FreqHz > 0)
            .Take(MaxItems)
            .ToList();
        if (list.Count == 0) return Array.Empty<CallStatusResult>();
        var index = await GetIndexAsync(ct).ConfigureAwait(false);
        var table = DxccTable.Default;
        return list.Select(i => Classify(index, i.Call!, i.FreqHz, table)).ToList();
    }

    internal static bool LooksLikeCall(string? call)
    {
        var c = (call ?? "").Trim().ToUpperInvariant();
        return c.Length is >= 3 and <= 15
            && c.All(ch => ch is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '/')
            && c.Any(char.IsDigit)
            && c.Any(char.IsLetter);
    }

    private async Task<CallStatusIndex?> GetIndexAsync(CancellationToken ct)
    {
        var plugin = _logbook.Current;
        if (plugin is null) return null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            bool sameSource = ReferenceEquals(plugin, _indexedFrom);
            if (sameSource && now - _lastCountCheck < CountCheckInterval)
                return _index;
            _lastCountCheck = now;
            var head = await plugin.GetEntriesAsync(0, 1, ct).ConfigureAwait(false);
            if (sameSource && head.TotalCount == _indexedCount)
                return _index;

            var all = new List<LogbookEntrySnapshot>(Math.Max(0, head.TotalCount));
            for (int skip = 0; ; skip += PageSize)
            {
                var page = await plugin.GetEntriesAsync(skip, PageSize, ct).ConfigureAwait(false);
                all.AddRange(page.Entries);
                if (page.Entries.Count < PageSize) break;
            }
            _index = BuildIndex(all, DxccTable.Default);
            _indexedFrom = plugin;
            _indexedCount = head.TotalCount;
            return _index;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
