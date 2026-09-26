// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// LDPC (174,91) belief-propagation decoder, ported from ft8_lib's ft8/ldpc.c
// (bp_decode — the one ft8_lib's decoder calls; Karlis Goba, MIT, see
// LICENSE.ft8_lib). Input is 174 log-likelihoods log(P(1)/P(0)); output is the
// hard-decision bits and the number of parity checks still failing (0 = a
// codeword). Float arithmetic and the rational tanh/atanh approximations are
// kept as the C.

namespace Zeus.Server.Hosting.Digital.Ft8;

internal static class FtxLdpc
{
    private const int N = FtxConstants.LdpcN;
    private const int M = FtxConstants.LdpcM;

    /// <summary>bp_decode(): returns the number of failing parity checks of the
    /// best hard decision (0 = success); <paramref name="plain"/> holds its bits.</summary>
    public static int BpDecode(ReadOnlySpan<float> codeword, int maxIters, Span<byte> plain)
    {
        var nm = FtxConstants.LdpcNm;
        var mn = FtxConstants.LdpcMn;
        var numRows = FtxConstants.LdpcNumRows;

        Span<float> tov = stackalloc float[N * 3];
        Span<float> toc = stackalloc float[M * 7];
        tov.Clear();

        int minErrors = M;
        for (int iter = 0; iter < maxIters; ++iter)
        {
            // Hard decision (tov = 0 on the first pass).
            int plainSum = 0;
            for (int n = 0; n < N; ++n)
            {
                plain[n] = codeword[n] + tov[n * 3] + tov[n * 3 + 1] + tov[n * 3 + 2] > 0 ? (byte)1 : (byte)0;
                plainSum += plain[n];
            }
            if (plainSum == 0) break;                 // all-zeros is not a valid message

            int errors = Check(plain);
            if (errors < minErrors)
            {
                minErrors = errors;
                if (errors == 0) break;
            }

            // Bits → check nodes.
            for (int m = 0; m < M; ++m)
            {
                for (int nIdx = 0; nIdx < numRows[m]; ++nIdx)
                {
                    int n = nm[m * 7 + nIdx] - 1;
                    float tnm = codeword[n];
                    for (int mIdx = 0; mIdx < 3; ++mIdx)
                        if (mn[n * 3 + mIdx] - 1 != m) tnm += tov[n * 3 + mIdx];
                    toc[m * 7 + nIdx] = FastTanh(-tnm / 2);
                }
            }

            // Check nodes → bits.
            for (int n = 0; n < N; ++n)
            {
                for (int mIdx = 0; mIdx < 3; ++mIdx)
                {
                    int m = mn[n * 3 + mIdx] - 1;
                    float tmn = 1.0f;
                    for (int nIdx = 0; nIdx < numRows[m]; ++nIdx)
                        if (nm[m * 7 + nIdx] - 1 != n) tmn *= toc[m * 7 + nIdx];
                    tov[n * 3 + mIdx] = -2 * FastAtanh(tmn);
                }
            }
        }
        return minErrors;
    }

    /// <summary>ldpc_check(): the number of parity checks the bits fail.</summary>
    public static int Check(ReadOnlySpan<byte> codeword)
    {
        var nm = FtxConstants.LdpcNm;
        var numRows = FtxConstants.LdpcNumRows;
        int errors = 0;
        for (int m = 0; m < M; ++m)
        {
            int x = 0;
            for (int i = 0; i < numRows[m]; ++i) x ^= codeword[nm[m * 7 + i] - 1];
            if (x != 0) ++errors;
        }
        return errors;
    }

    private static float FastTanh(float x)
    {
        if (x < -4.97f) return -1.0f;
        if (x > 4.97f) return 1.0f;
        float x2 = x * x;
        float a = x * (945.0f + x2 * (105.0f + x2));
        float b = 945.0f + x2 * (420.0f + x2 * 15.0f);
        return a / b;
    }

    private static float FastAtanh(float x)
    {
        float x2 = x * x;
        float a = x * (945.0f + x2 * (-735.0f + x2 * 64.0f));
        float b = 945.0f + x2 * (-1050.0f + x2 * 225.0f);
        return a / b;
    }
}
