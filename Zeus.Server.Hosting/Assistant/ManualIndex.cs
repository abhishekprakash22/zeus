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
// ManualIndex — the operator manual's chapters (docs/manual/chapters/*.md,
// embedded in this assembly at build time) split into heading sections and
// searched with a small TF-IDF scorer. The assistant answers from what this
// returns and cites the section it came from, so the manual stays the source
// of truth and a wrong answer is easy to check. No embeddings, no model on
// the radio: a few hundred sections score in well under a millisecond.

using System.Text;
using System.Text.RegularExpressions;

namespace Zeus.Server.Assistant;

public sealed record ManualSection(string Chapter, string Heading, string Text)
{
    /// <summary>"Chapter title › Section heading" — what the UI shows as the citation.</summary>
    public string Citation => string.Equals(Chapter, Heading, StringComparison.Ordinal) ? Chapter : $"{Chapter} › {Heading}";
}

public sealed record ManualHit(ManualSection Section, double Score);

public sealed partial class ManualIndex
{
    public const string ResourcePrefix = "Zeus.Manual.";
    private const int MaxSectionChars = 1600;

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "you", "your", "are", "with", "that", "this", "what", "how", "does",
        "why", "can", "not", "but", "its", "it's", "from", "into", "when", "will", "have", "has",
        "is", "to", "of", "in", "on", "a", "an", "or", "be", "do", "my", "i", "me", "it", "at", "by",
    };

    private readonly List<ManualSection> _sections = new();
    private readonly List<Dictionary<string, int>> _tf = new();
    private readonly List<HashSet<string>> _headingTerms = new();
    private readonly Dictionary<string, int> _df = new(StringComparer.Ordinal);

    public int Count => _sections.Count;

    /// <summary>Build from (name, markdown) chapters — the embedded set, or test fixtures.</summary>
    public ManualIndex(IEnumerable<(string Name, string Markdown)> chapters)
    {
        foreach (var (name, md) in chapters.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            foreach (var s in SplitSections(name, md)) Add(s);
        }
    }

    /// <summary>The manual chapters embedded in this assembly (empty if none).</summary>
    public static ManualIndex LoadEmbedded()
    {
        var asm = typeof(ManualIndex).Assembly;
        var chapters = new List<(string, string)>();
        foreach (var res in asm.GetManifestResourceNames())
        {
            if (!res.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                !res.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
            using var stream = asm.GetManifestResourceStream(res);
            if (stream is null) continue;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            chapters.Add((res[ResourcePrefix.Length..], reader.ReadToEnd()));
        }
        return new ManualIndex(chapters);
    }

    /// <summary>Top sections for a question, best first. Empty when nothing matches.</summary>
    public IReadOnlyList<ManualHit> Search(string query, int max = 3)
    {
        var terms = Tokenize(query).Distinct(StringComparer.Ordinal).ToList();
        if (terms.Count == 0 || _sections.Count == 0) return Array.Empty<ManualHit>();
        var n = (double)_sections.Count;
        var hits = new List<ManualHit>();
        for (var i = 0; i < _sections.Count; i++)
        {
            double score = 0;
            foreach (var t in terms)
            {
                if (!_df.TryGetValue(t, out var df)) continue;
                var idf = Math.Log(1 + n / df);
                if (_tf[i].TryGetValue(t, out var tf)) score += (1 + Math.Log(tf)) * idf;
                if (_headingTerms[i].Contains(t)) score += 2.5 * idf;
            }
            if (score > 0) hits.Add(new ManualHit(_sections[i], score));
        }
        return hits.OrderByDescending(h => h.Score).Take(Math.Clamp(max, 1, 8)).ToList();
    }

    public static IReadOnlyList<string> Tokenize(string text) =>
        WordRx().Matches(text.ToLowerInvariant())
            .Select(m => m.Value.Trim('-'))
            .Where(w => w.Length >= 2 && !Stop.Contains(w))
            .ToList();

    /// <summary>Split a chapter on markdown headings; the first # heading names the chapter.</summary>
    public static IEnumerable<ManualSection> SplitSections(string name, string markdown)
    {
        var chapter = Path.GetFileNameWithoutExtension(name);
        var heading = chapter;
        var body = new StringBuilder();
        var titled = false;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var m = HeadingRx().Match(raw);
            if (m.Success)
            {
                if (body.ToString().Trim().Length > 0) yield return Make(chapter, heading, body.ToString());
                body.Clear();
                var title = m.Groups[2].Value.Trim();
                if (!titled && m.Groups[1].Value.Length == 1)
                {
                    chapter = title;
                    titled = true;
                }
                heading = title;
                continue;
            }
            body.AppendLine(raw);
        }
        if (body.ToString().Trim().Length > 0) yield return Make(chapter, heading, body.ToString());
    }

    private static ManualSection Make(string chapter, string heading, string text)
    {
        var t = text.Trim();
        if (t.Length > MaxSectionChars) t = t[..MaxSectionChars] + " …";
        return new ManualSection(chapter, heading, t);
    }

    private void Add(ManualSection s)
    {
        var tf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var w in Tokenize(s.Heading + " " + s.Text))
        {
            tf[w] = tf.TryGetValue(w, out var c) ? c + 1 : 1;
        }
        foreach (var w in tf.Keys) _df[w] = _df.TryGetValue(w, out var d) ? d + 1 : 1;
        _sections.Add(s);
        _tf.Add(tf);
        _headingTerms.Add(new HashSet<string>(Tokenize(s.Heading), StringComparer.Ordinal));
    }

    [GeneratedRegex(@"[a-z0-9][a-z0-9\-\.]*[a-z0-9]|[a-z0-9]")]
    private static partial Regex WordRx();

    [GeneratedRegex(@"^(#{1,4})\s+(.+?)\s*#*\s*$")]
    private static partial Regex HeadingRx();
}
