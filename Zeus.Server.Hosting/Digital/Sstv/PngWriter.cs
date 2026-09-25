// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// Minimal PNG encoder for SSTV pictures: 8-bit RGB, one IDAT, "Up" filter on
// every row (SSTV pictures are vertically smooth, so it roughly halves the
// file against no filter), optional tEXt chunks. zlib from the BCL
// (ZLibStream) and a table CRC-32 — no image library, no new dependency.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Zeus.Server.Hosting.Digital.Sstv;

public static class PngWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] EncodeRgb(
        ReadOnlySpan<byte> rgb, int width, int height,
        IReadOnlyList<KeyValuePair<string, string>>? text = null)
    {
        if (rgb.Length < width * height * 3)
            throw new ArgumentException("rgb shorter than width × height × 3", nameof(rgb));

        using var ms = new MemoryStream();
        ms.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 2;   // colour type: truecolour
        ihdr[10] = 0;  // deflate
        ihdr[11] = 0;  // adaptive filtering
        ihdr[12] = 0;  // no interlace
        WriteChunk(ms, "IHDR", ihdr);

        if (text is not null)
            foreach (var (key, value) in text)
            {
                // tEXt: Latin-1 keyword (1–79 chars), NUL, Latin-1 text.
                var kv = Encoding.Latin1.GetBytes($"{key}\0{value}");
                WriteChunk(ms, "tEXt", kv);
            }

        int stride = width * 3;
        var raw = new byte[height * (stride + 1)];
        for (int y = 0; y < height; y++)
        {
            int o = y * (stride + 1);
            var row = rgb.Slice(y * stride, stride);
            if (y == 0)
            {
                raw[o] = 0;                                     // None
                row.CopyTo(raw.AsSpan(o + 1));
            }
            else
            {
                raw[o] = 2;                                     // Up
                var prev = rgb.Slice((y - 1) * stride, stride);
                for (int i = 0; i < stride; i++) raw[o + 1 + i] = (byte)(row[i] - prev[i]);
            }
        }

        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true))
                zs.Write(raw);
            WriteChunk(ms, "IDAT", z.GetBuffer().AsSpan(0, (int)z.Length));
        }
        WriteChunk(ms, "IEND", ReadOnlySpan<byte>.Empty);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> hdr = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(hdr, data.Length);
        Encoding.ASCII.GetBytes(type, hdr[4..]);
        s.Write(hdr);
        s.Write(data);
        uint crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, hdr[4..]), data) ^ 0xFFFFFFFFu;
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }

    private static class Crc32
    {
        private static readonly uint[] Table = Build();

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static uint[] Build()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }
    }
}
