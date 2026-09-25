// SPDX-License-Identifier: GPL-2.0-or-later
//
// SSTV gallery: the PNG must be a real PNG (decoded here with nothing but
// zlib, CRCs checked), pictures must survive a reload, and a key must never
// escape the gallery directory.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Zeus.Server.Hosting.Digital.Sstv;

namespace Zeus.Server.Tests;

public sealed class SstvGalleryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zeus-sstv-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Png_RoundTrips_WithValidCrcs_AndText()
    {
        const int w = 37, h = 11;
        var rgb = new byte[w * h * 3];
        new Random(5).NextBytes(rgb);
        var png = PngWriter.EncodeRgb(rgb, w, h, [new("Comment", "Martin 1, 14.230 MHz USB")]);

        var (gotW, gotH, pixels, text) = DecodePng(png);
        Assert.Equal((w, h), (gotW, gotH));
        Assert.Equal(rgb, pixels);
        Assert.Equal("Martin 1, 14.230 MHz USB", text["Comment"]);
    }

    [Fact]
    public void Gallery_SaveLoadDelete()
    {
        var g = new SstvGallery(_dir);
        var older = Meta(SstvGallery.MakeKey(1_700_000_000_000, 14_230_000, "Martin 1"), 1_700_000_000_000);
        var newer = Meta(SstvGallery.MakeKey(1_700_000_100_000, 7_171_000, "PD 120"), 1_700_000_100_000);
        g.Save(older, new byte[2 * 2 * 3]);
        g.Save(newer, new byte[2 * 2 * 3]);

        Assert.Equal("20231114-221320Z_14230kHz_Martin1", older.Key);
        var idx = g.LoadIndex(10);
        Assert.Equal([newer.Key, older.Key], idx.Select(m => m.Key));
        Assert.Equal("EA4ABC", idx[0].Callsign);
        Assert.NotNull(g.ReadPng(older.Key));

        g.Delete(older.Key);
        Assert.Equal([newer.Key], g.LoadIndex(10).Select(m => m.Key));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Gallery_RejectsKeysThatCouldEscape(string key)
    {
        var g = new SstvGallery(_dir);
        Assert.Throws<ArgumentException>(() => g.ReadPng(key));
    }

    private static SstvStoredMeta Meta(string key, long started) => new(
        key, "Martin 1", 2, 2, 2, 0, 0, 14_230_000, "USB", started, started + 1000,
        "Complete", 0, 0, "EA4ABC");

    [Fact]
    public void Gallery_ReadsSidecarsWrittenBeforeViaSyncExisted()
    {
        Directory.CreateDirectory(_dir);
        var key = SstvGallery.MakeKey(1_700_000_000_000, 14_230_000, "Martin 1");
        File.WriteAllBytes(Path.Combine(_dir, key + ".png"), PngWriter.EncodeRgb(new byte[12], 2, 2));
        File.WriteAllText(Path.Combine(_dir, key + ".json"), $$"""
            {"key":"{{key}}","mode":"Martin 1","width":2,"height":2,"rowsDone":2,"offsetHz":0,
             "clockErrorPpm":0,"dialHz":14230000,"sideBand":"USB","startedUnixMs":1700000000000,
             "endedUnixMs":null,"endReason":"Complete","slantPpm":0,"shiftPx":0,"callsign":null}
            """);
        var m = Assert.Single(new SstvGallery(_dir).LoadIndex(10));
        Assert.False(m.ViaSync);
    }

    // ---- a PNG reader just capable enough to check what we write -----------

    private static (int W, int H, byte[] Rgb, Dictionary<string, string> Text) DecodePng(byte[] png)
    {
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        int pos = 8, w = 0, h = 0;
        var idat = new MemoryStream();
        var text = new Dictionary<string, string>();
        while (pos < png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            string type = Encoding.ASCII.GetString(png, pos + 4, 4);
            var data = png.AsSpan(pos + 8, len);
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 8 + len));
            Assert.Equal(Crc(png.AsSpan(pos + 4, 4 + len)), crc);
            if (type == "IHDR")
            {
                w = BinaryPrimitives.ReadInt32BigEndian(data);
                h = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                Assert.Equal(8, data[8]);
                Assert.Equal(2, data[9]);
            }
            else if (type == "IDAT") idat.Write(data);
            else if (type == "tEXt")
            {
                var kv = Encoding.Latin1.GetString(data).Split('\0');
                text[kv[0]] = kv[1];
            }
            pos += 12 + len;
        }

        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        var raw = new MemoryStream();
        z.CopyTo(raw);
        var r = raw.ToArray();
        int stride = w * 3;
        var rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            byte filter = r[y * (stride + 1)];
            for (int i = 0; i < stride; i++)
            {
                byte v = r[y * (stride + 1) + 1 + i];
                rgb[y * stride + i] = filter switch
                {
                    0 => v,
                    2 => (byte)(v + (y > 0 ? rgb[(y - 1) * stride + i] : 0)),
                    _ => throw new InvalidDataException($"unexpected filter {filter}"),
                };
            }
        }
        return (w, h, rgb, text);
    }

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFF;
    }
}
