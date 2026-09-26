// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// Forward FFTs ported from KISS FFT (Copyright (c) 2003-2010, Mark
// Borgerding; BSD-3-Clause, see LICENSE.kissfft), the library ft8_lib
// vendored: kiss_fft (mixed radix 4/2/3/5/generic) and kiss_fftr (a real FFT
// through a half-size complex one). Ported butterfly for
// butterfly, with the same twiddles (double cos/sin rounded to float) and the
// same float operation order, so the FT8 waterfall is built the way the native
// monitor built it. Only what ft8_lib uses is here: float, forward, stride 1.

namespace Zeus.Server.Hosting.Digital.Ft8;

internal struct Cpx
{
    public float R, I;
    public Cpx(float r, float i) { R = r; I = i; }
}

internal sealed class KissFft
{
    private readonly int _nfft;
    private readonly Cpx[] _twiddles;
    private readonly int[] _factors = new int[64];

    public KissFft(int nfft)
    {
        _nfft = nfft;
        _twiddles = new Cpx[nfft];
        const double pi = 3.141592653589793238462643383279502884197169399375105820974944;
        for (int i = 0; i < nfft; ++i)
        {
            double phase = -2 * pi * i / nfft;
            _twiddles[i] = new Cpx((float)Math.Cos(phase), (float)Math.Sin(phase));
        }
        Factor(nfft, _factors);
    }

    public int Size => _nfft;

    /// <summary>kiss_fft(): <paramref name="fin"/> and <paramref name="fout"/> must not overlap.</summary>
    public void Transform(ReadOnlySpan<Cpx> fin, Span<Cpx> fout) => Work(fout, 0, fin, 0, 1, 0);

    /// <summary>kf_factor(): powers of 4, then 2, then the remaining primes.</summary>
    private static void Factor(int n, int[] facbuf)
    {
        int p = 4, k = 0;
        double floorSqrt = Math.Floor(Math.Sqrt(n));
        do
        {
            while (n % p != 0)
            {
                p = p switch { 4 => 2, 2 => 3, _ => p + 2 };
                if (p > floorSqrt) p = n;
            }
            n /= p;
            facbuf[k++] = p;
            facbuf[k++] = n;
        } while (n > 1);
    }

    private void Work(Span<Cpx> fout, int o, ReadOnlySpan<Cpx> f, int fi, int fstride, int fac)
    {
        int p = _factors[fac];
        int m = _factors[fac + 1];
        int oEnd = o + p * m;
        int oBeg = o;

        if (m == 1)
        {
            do
            {
                fout[o] = f[fi];
                fi += fstride;
            } while (++o != oEnd);
        }
        else
        {
            do
            {
                Work(fout, o, f, fi, fstride * p, fac + 2);
                fi += fstride;
            } while ((o += m) != oEnd);
        }

        o = oBeg;
        switch (p)
        {
            case 2: Bfly2(fout, o, fstride, m); break;
            case 3: Bfly3(fout, o, fstride, m); break;
            case 4: Bfly4(fout, o, fstride, m); break;
            case 5: Bfly5(fout, o, fstride, m); break;
            default: BflyGeneric(fout, o, fstride, m, p); break;
        }
    }

    private static Cpx Mul(Cpx a, Cpx b) => new(a.R * b.R - a.I * b.I, a.R * b.I + a.I * b.R);

    private void Bfly2(Span<Cpx> f, int o, int fstride, int m)
    {
        int o2 = o + m, tw = 0;
        do
        {
            Cpx t = Mul(f[o2], _twiddles[tw]);
            tw += fstride;
            f[o2] = new Cpx(f[o].R - t.R, f[o].I - t.I);
            f[o] = new Cpx(f[o].R + t.R, f[o].I + t.I);
            ++o2;
            ++o;
        } while (--m != 0);
    }

    private void Bfly4(Span<Cpx> f, int o, int fstride, int m)
    {
        int tw1 = 0, tw2 = 0, tw3 = 0;
        int k = m, m2 = 2 * m, m3 = 3 * m;
        do
        {
            Cpx s0 = Mul(f[o + m], _twiddles[tw1]);
            Cpx s1 = Mul(f[o + m2], _twiddles[tw2]);
            Cpx s2 = Mul(f[o + m3], _twiddles[tw3]);

            var s5 = new Cpx(f[o].R - s1.R, f[o].I - s1.I);
            f[o] = new Cpx(f[o].R + s1.R, f[o].I + s1.I);
            var s3 = new Cpx(s0.R + s2.R, s0.I + s2.I);
            var s4 = new Cpx(s0.R - s2.R, s0.I - s2.I);
            f[o + m2] = new Cpx(f[o].R - s3.R, f[o].I - s3.I);
            tw1 += fstride;
            tw2 += fstride * 2;
            tw3 += fstride * 3;
            f[o] = new Cpx(f[o].R + s3.R, f[o].I + s3.I);

            f[o + m] = new Cpx(s5.R + s4.I, s5.I - s4.R);           // forward transform
            f[o + m3] = new Cpx(s5.R - s4.I, s5.I + s4.R);
            ++o;
        } while (--k != 0);
    }

    private void Bfly3(Span<Cpx> f, int o, int fstride, int m)
    {
        int k = m, m2 = 2 * m;
        int tw1 = 0, tw2 = 0;
        Cpx epi3 = _twiddles[fstride * m];
        do
        {
            Cpx s1 = Mul(f[o + m], _twiddles[tw1]);
            Cpx s2 = Mul(f[o + m2], _twiddles[tw2]);
            var s3 = new Cpx(s1.R + s2.R, s1.I + s2.I);
            var s0 = new Cpx(s1.R - s2.R, s1.I - s2.I);
            tw1 += fstride;
            tw2 += fstride * 2;

            // HALF_OF(x) is x * .5 in double; a float minus an exact half,
            // rounded once to float, equals the float subtraction.
            f[o + m] = new Cpx(f[o].R - s3.R * 0.5f, f[o].I - s3.I * 0.5f);
            s0 = new Cpx(s0.R * epi3.I, s0.I * epi3.I);
            f[o] = new Cpx(f[o].R + s3.R, f[o].I + s3.I);

            f[o + m2] = new Cpx(f[o + m].R + s0.I, f[o + m].I - s0.R);
            f[o + m] = new Cpx(f[o + m].R - s0.I, f[o + m].I + s0.R);
            ++o;
        } while (--k != 0);
    }

    private void Bfly5(Span<Cpx> f, int o, int fstride, int m)
    {
        Cpx ya = _twiddles[fstride * m];
        Cpx yb = _twiddles[fstride * 2 * m];
        int o0 = o, o1 = o + m, o2 = o + 2 * m, o3 = o + 3 * m, o4 = o + 4 * m;

        for (int u = 0; u < m; ++u)
        {
            Cpx s0 = f[o0];
            Cpx s1 = Mul(f[o1], _twiddles[u * fstride]);
            Cpx s2 = Mul(f[o2], _twiddles[2 * u * fstride]);
            Cpx s3 = Mul(f[o3], _twiddles[3 * u * fstride]);
            Cpx s4 = Mul(f[o4], _twiddles[4 * u * fstride]);

            var s7 = new Cpx(s1.R + s4.R, s1.I + s4.I);
            var s10 = new Cpx(s1.R - s4.R, s1.I - s4.I);
            var s8 = new Cpx(s2.R + s3.R, s2.I + s3.I);
            var s9 = new Cpx(s2.R - s3.R, s2.I - s3.I);

            f[o0] = new Cpx(f[o0].R + (s7.R + s8.R), f[o0].I + (s7.I + s8.I));

            var s5 = new Cpx(s0.R + s7.R * ya.R + s8.R * yb.R, s0.I + s7.I * ya.R + s8.I * yb.R);
            var s6 = new Cpx(s10.I * ya.I + s9.I * yb.I, -(s10.R * ya.I) - s9.R * yb.I);

            f[o1] = new Cpx(s5.R - s6.R, s5.I - s6.I);
            f[o4] = new Cpx(s5.R + s6.R, s5.I + s6.I);

            var s11 = new Cpx(s0.R + s7.R * yb.R + s8.R * ya.R, s0.I + s7.I * yb.R + s8.I * ya.R);
            var s12 = new Cpx(-(s10.I * yb.I) + s9.I * ya.I, s10.R * yb.I - s9.R * ya.I);

            f[o2] = new Cpx(s11.R + s12.R, s11.I + s12.I);
            f[o3] = new Cpx(s11.R - s12.R, s11.I - s12.I);

            ++o0; ++o1; ++o2; ++o3; ++o4;
        }
    }

    private void BflyGeneric(Span<Cpx> f, int o, int fstride, int m, int p)
    {
        Span<Cpx> scratch = p <= 64 ? stackalloc Cpx[p] : new Cpx[p];
        for (int u = 0; u < m; ++u)
        {
            int k = u;
            for (int q1 = 0; q1 < p; ++q1)
            {
                scratch[q1] = f[o + k];
                k += m;
            }

            k = u;
            for (int q1 = 0; q1 < p; ++q1)
            {
                int twidx = 0;
                f[o + k] = scratch[0];
                for (int q = 1; q < p; ++q)
                {
                    twidx += fstride * k;
                    if (twidx >= _nfft) twidx -= _nfft;
                    Cpx t = Mul(scratch[q], _twiddles[twidx]);
                    f[o + k] = new Cpx(f[o + k].R + t.R, f[o + k].I + t.I);
                }
                k += m;
            }
        }
    }
}

/// <summary>kiss_fftr: forward real FFT of even length n → n/2 + 1 bins.</summary>
internal sealed class KissFftr
{
    private readonly KissFft _sub;
    private readonly Cpx[] _tmp;
    private readonly Cpx[] _packed;
    private readonly Cpx[] _superTwiddles;

    public KissFftr(int nfft)
    {
        if ((nfft & 1) != 0) throw new ArgumentException("Real FFT length must be even", nameof(nfft));
        int ncfft = nfft >> 1;
        _sub = new KissFft(ncfft);
        _tmp = new Cpx[ncfft];
        _packed = new Cpx[ncfft];
        _superTwiddles = new Cpx[ncfft / 2];
        for (int i = 0; i < ncfft / 2; ++i)
        {
            double phase = -3.14159265358979323846264338327 * ((double)(i + 1) / ncfft + .5);
            _superTwiddles[i] = new Cpx((float)Math.Cos(phase), (float)Math.Sin(phase));
        }
    }

    public int Size => _sub.Size * 2;

    public void Transform(ReadOnlySpan<float> timedata, Span<Cpx> freqdata)
    {
        int ncfft = _sub.Size;
        for (int i = 0; i < ncfft; i++) _packed[i] = new Cpx(timedata[2 * i], timedata[2 * i + 1]);
        _sub.Transform(_packed, _tmp);

        Cpx tdc = _tmp[0];
        freqdata[0] = new Cpx(tdc.R + tdc.I, 0);
        freqdata[ncfft] = new Cpx(tdc.R - tdc.I, 0);

        for (int k = 1; k <= ncfft / 2; ++k)
        {
            Cpx fpk = _tmp[k];
            var fpnk = new Cpx(_tmp[ncfft - k].R, -_tmp[ncfft - k].I);
            var f1k = new Cpx(fpk.R + fpnk.R, fpk.I + fpnk.I);
            var f2k = new Cpx(fpk.R - fpnk.R, fpk.I - fpnk.I);
            Cpx tw = new(f2k.R * _superTwiddles[k - 1].R - f2k.I * _superTwiddles[k - 1].I,
                         f2k.R * _superTwiddles[k - 1].I + f2k.I * _superTwiddles[k - 1].R);
            freqdata[k] = new Cpx((f1k.R + tw.R) * 0.5f, (f1k.I + tw.I) * 0.5f);
            freqdata[ncfft - k] = new Cpx((f1k.R - tw.R) * 0.5f, (tw.I - f1k.I) * 0.5f);
        }
    }
}
