// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// Shared ÷4 decimator (48 → 12 kHz) for the in-core RX-audio consumers —
// WSPR and SSTV both tap DspPipelineService.RxAudioAvailable at 48 kHz and
// decode at 12 kHz. Lifted out of WsprService unchanged.

namespace Zeus.Server.Hosting.Digital;

/// <summary>÷4 decimator, 48 → 12 kHz. Allocation-free; writes directly
/// into the caller's power-of-two ring at a running index.</summary>
internal sealed class Decimator4
{
    private const int Taps = 48;
    private readonly float[] _h = BuildTaps();
    private readonly float[] _delay = new float[Taps];
    private int _pos, _phase;

    public int Process(ReadOnlySpan<float> in48k, float[] ring, long writeIndex, int ringLen)
    {
        int produced = 0;
        for (int i = 0; i < in48k.Length; i++)
        {
            _delay[_pos] = in48k[i];
            _pos = _pos + 1 == Taps ? 0 : _pos + 1;
            if (++_phase == 4)
            {
                _phase = 0;
                float acc = 0f;
                int idx = _pos;
                for (int t = Taps - 1; t >= 0; t--)
                {
                    acc += _delay[idx] * _h[t];
                    idx = idx + 1 == Taps ? 0 : idx + 1;
                }
                ring[(writeIndex + produced) & (ringLen - 1)] = acc;
                produced++;
            }
        }
        return produced;
    }

    public void Reset() { Array.Clear(_delay); _pos = 0; _phase = 0; }

    private static float[] BuildTaps()
    {
        var h = new double[Taps];
        double fc = 5_000.0 / 48_000.0, sum = 0;
        for (int i = 0; i < Taps; i++)
        {
            double m = i - (Taps - 1) / 2.0;
            double sinc = m == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * m) / (Math.PI * m);
            double win = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (Taps - 1));
            h[i] = sinc * win; sum += h[i];
        }
        var f = new float[Taps];
        for (int i = 0; i < Taps; i++) f[i] = (float)(h[i] / sum);
        return f;
    }
}
