// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// 77-bit payload → channel tones, ported from ft8_lib's ft8/encode.c and
// ft8/crc.c (Karlis Goba, MIT; see LICENSE.ft8_lib): CRC-14, the (174,91)
// LDPC code, then the Gray-mapped tone sequence with its Costas sync groups.
// FT8 sends 79 8-FSK tones, FT4 105 4-FSK tones (payload XOR-whitened first).

namespace Zeus.Server.Hosting.Digital.Ft8;

internal static class FtxCrc
{
    private const int TopBit = 1 << (FtxConstants.CrcWidth - 1);

    /// <summary>ftx_compute_crc(): CRC-14 over the first <paramref name="numBits"/> bits (MSB first).</summary>
    public static ushort Compute(ReadOnlySpan<byte> message, int numBits)
    {
        int remainder = 0;
        int idxByte = 0;
        for (int idxBit = 0; idxBit < numBits; ++idxBit)
        {
            if (idxBit % 8 == 0)
            {
                remainder ^= message[idxByte] << (FtxConstants.CrcWidth - 8);
                ++idxByte;
            }
            remainder = (remainder & TopBit) != 0
                ? ((remainder << 1) ^ FtxConstants.CrcPolynomial) & 0xFFFF
                : (remainder << 1) & 0xFFFF;
        }
        return (ushort)(remainder & ((TopBit << 1) - 1));
    }

    /// <summary>ftx_extract_crc(): the 14 CRC bits after the 77-bit payload.</summary>
    public static ushort Extract(ReadOnlySpan<byte> a91) =>
        (ushort)(((a91[9] & 0x07) << 11) | (a91[10] << 3) | (a91[11] >> 5));

    /// <summary>ftx_add_crc(): 77 payload bits + CRC-14 of them zero-extended to 82 bits.</summary>
    public static void Add(ReadOnlySpan<byte> payload, Span<byte> a91)
    {
        for (int i = 0; i < 10; i++) a91[i] = payload[i];
        a91[9] &= 0xF8;
        a91[10] = 0;

        ushort checksum = Compute(a91, 96 - 14);

        a91[9] |= (byte)(checksum >> 11);
        a91[10] = (byte)(checksum >> 3);
        a91[11] = (byte)(checksum << 5);
    }
}

internal static class FtxEncoder
{
    private static int Parity8(byte x)
    {
        x ^= (byte)(x >> 4);
        x ^= (byte)(x >> 2);
        x ^= (byte)(x >> 1);
        return x & 1;
    }

    /// <summary>encode174(): 91 message bits (12 bytes) → 174-bit codeword (22 bytes).</summary>
    public static void Encode174(ReadOnlySpan<byte> message, Span<byte> codeword)
    {
        for (int j = 0; j < FtxConstants.LdpcNBytes; ++j)
            codeword[j] = j < FtxConstants.LdpcKBytes ? message[j] : (byte)0;

        byte colMask = (byte)(0x80 >> (FtxConstants.LdpcK % 8));
        int colIdx = FtxConstants.LdpcKBytes - 1;
        var gen = FtxConstants.LdpcGenerator;

        for (int i = 0; i < FtxConstants.LdpcM; ++i)
        {
            int nsum = 0;
            for (int j = 0; j < FtxConstants.LdpcKBytes; ++j)
                nsum ^= Parity8((byte)(message[j] & gen[i * FtxConstants.LdpcKBytes + j]));

            if ((nsum & 1) != 0) codeword[colIdx] |= colMask;

            colMask >>= 1;
            if (colMask == 0)
            {
                colMask = 0x80;
                ++colIdx;
            }
        }
    }

    /// <summary>ft8_encode(): 10-byte payload → 79 tones (0..7).</summary>
    public static void Ft8Tones(ReadOnlySpan<byte> payload, Span<byte> tones)
    {
        Span<byte> a91 = stackalloc byte[FtxConstants.LdpcKBytes];
        FtxCrc.Add(payload, a91);
        Span<byte> codeword = stackalloc byte[FtxConstants.LdpcNBytes];
        Encode174(a91, codeword);

        var bits = new BitReader(codeword);
        for (int i = 0; i < FtxConstants.Ft8Nn; ++i)
        {
            if (i < 7) tones[i] = FtxConstants.Ft8Costas[i];
            else if (i >= 36 && i < 43) tones[i] = FtxConstants.Ft8Costas[i - 36];
            else if (i >= 72 && i < 79) tones[i] = FtxConstants.Ft8Costas[i - 72];
            else tones[i] = FtxConstants.Ft8Gray[(bits.Next() << 2) | (bits.Next() << 1) | bits.Next()];
        }
    }

    /// <summary>ft4_encode(): 10-byte payload → 105 tones (0..3).</summary>
    public static void Ft4Tones(ReadOnlySpan<byte> payload, Span<byte> tones)
    {
        Span<byte> payloadXor = stackalloc byte[10];
        for (int i = 0; i < 10; ++i) payloadXor[i] = (byte)(payload[i] ^ FtxConstants.Ft4Xor[i]);

        Span<byte> a91 = stackalloc byte[FtxConstants.LdpcKBytes];
        FtxCrc.Add(payloadXor, a91);
        Span<byte> codeword = stackalloc byte[FtxConstants.LdpcNBytes];
        Encode174(a91, codeword);

        var bits = new BitReader(codeword);
        for (int i = 0; i < FtxConstants.Ft4Nn; ++i)
        {
            if (i == 0 || i == 104) tones[i] = 0;                      // ramp
            else if (i >= 1 && i < 5) tones[i] = FtxConstants.Ft4Costas[0 * 4 + i - 1];
            else if (i >= 34 && i < 38) tones[i] = FtxConstants.Ft4Costas[1 * 4 + i - 34];
            else if (i >= 67 && i < 71) tones[i] = FtxConstants.Ft4Costas[2 * 4 + i - 67];
            else if (i >= 100 && i < 104) tones[i] = FtxConstants.Ft4Costas[3 * 4 + i - 100];
            else tones[i] = FtxConstants.Ft4Gray[(bits.Next() << 1) | bits.Next()];
        }
    }

    /// <summary>MSB-first bit cursor over a codeword. C# evaluates operands
    /// left to right, so (Next() &lt;&lt; 2) | (Next() &lt;&lt; 1) | Next() reads in order.</summary>
    private ref struct BitReader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private int _bit;

        public int Next()
        {
            int b = (_bytes[_bit >> 3] >> (7 - (_bit & 7))) & 1;
            _bit++;
            return b;
        }
    }
}
