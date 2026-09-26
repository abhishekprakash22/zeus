// SPDX-License-Identifier: GPL-2.0-or-later
//
// Golden tests: the managed FT8/FT4 decoder on slots recorded off the air
// (HL2, 20 m, 12 kHz float32), against what the native libzeus_ft8 decoded
// on the same audio — frozen into <slot>.native.tsv before native/ft8 was
// retired. The native side decoded the slots in name order in one fresh
// session, so its callsign table filled the same way the one here does.

using System.Buffers.Binary;
using System.Globalization;
using Zeus.Server.Hosting.Digital;
using Zeus.Server.Hosting.Digital.Ft8;

namespace Zeus.Server.Tests;

public sealed class Ft8GoldenTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "TestData", "ft8");

    internal static string Line(Ft8DecodeDto d) => string.Create(CultureInfo.InvariantCulture,
        $"{d.Text}\t{d.FreqHz}\t{d.DtSec:0.00}\t{d.SnrDb}\t{d.Score}");

    private static float[] ReadF32(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var audio = new float[bytes.Length / 4];
        for (int i = 0; i < audio.Length; i++) audio[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4));
        return audio;
    }

    [Fact]
    public void RecordedSlots_MatchTheFrozenNativeDecodes()
    {
        var slots = Directory.GetFiles(Dir, "*.f32").OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.True(slots.Count >= 8, $"only {slots.Count} recorded slots");

        var table = new FtxCallsignTable();
        int total = 0;
        foreach (var f32 in slots)
        {
            bool isFt4 = f32.EndsWith("_FT4.f32", StringComparison.Ordinal);
            var got = Ft8Managed.ToDtos(FtxDecoder.Decode(ReadF32(f32), FtxDecoder.DecodeRate, isFt4, table))
                .Select(Line).ToList();
            var want = File.ReadAllLines(Path.ChangeExtension(f32, ".native.tsv")).ToList();
            Assert.True(want.SequenceEqual(got),
                $"{Path.GetFileName(f32)}\nnative:  {string.Join(" | ", want)}\nmanaged: {string.Join(" | ", got)}");
            total += got.Count;
        }
        Assert.True(total >= 100, $"only {total} decodes across the corpus");
    }

    [Fact]
    public void RecordedSlots_IncludeTheQsoMessagesToUs()
    {
        var table = new FtxCallsignTable();
        var ft8 = Ft8Managed.ToDtos(FtxDecoder.Decode(ReadF32(Path.Combine(Dir, "1790446815000_FT8.f32")), 12000, false, table));
        Assert.Contains(ft8, d => d.Text == "EA5IUE ON3URT -13");
        var ft4 = Ft8Managed.ToDtos(FtxDecoder.Decode(ReadF32(Path.Combine(Dir, "1790446980000_FT4.f32")), 12000, true, table));
        Assert.Contains(ft4, d => d.Text == "EA5IUE ON5RJ RR73");
    }
}
