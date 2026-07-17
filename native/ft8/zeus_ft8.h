/* SPDX-License-Identifier: GPL-2.0-or-later
 *
 * zeus_ft8 — flat FT8/FT4 decode + synth API for P/Invoke from Zeus.
 *
 * Built over ft8_lib (Karlis Goba, MIT). See zeus_ft8.c.
 */
#ifndef ZEUS_FT8_H
#define ZEUS_FT8_H

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#  define ZEUS_FT8_EXPORT __declspec(dllexport)
#else
#  define ZEUS_FT8_EXPORT __attribute__((visibility("default")))
#endif

/* One decoded message. Blittable: mirrors the C# struct field-for-field. */
typedef struct
{
    int   snr_db;
    float dt_sec;
    float freq_hz;
    int   score;
    char  text[40];
} zeus_ft8_decode_t;

/* ABI probe. Returns 1 for this revision (synth is an ADDITIVE export — the
 * C# side probes for it separately, so the version stays 1). */
ZEUS_FT8_EXPORT int zeus_ft8_version(void);

/*
 * Decode one slot.
 *   audio       - mono float32, any sample rate (resampled to 12 kHz internally)
 *   n_samples   - samples in `audio`
 *   sample_rate - rate of `audio` (e.g. 48000)
 *   is_ft4      - 0 = FT8, 1 = FT4
 *   out/max_out - caller-allocated array
 * Returns the number of decodes written (0..max_out). Never throws; on any
 * internal failure returns 0.
 */
ZEUS_FT8_EXPORT int zeus_ft8_decode(const float* audio, int n_samples,
                                    int sample_rate, int is_ft4,
                                    zeus_ft8_decode_t* out, int max_out);

/*
 * Synthesize the GFSK waveform for one FT8/FT4 transmission.
 *   text        - standard FT8/FT4 message text ("CQ VU3NWZ FN42",
 *                 "VU3NWZ K1ABC -09", ...). Encoded verbatim by ft8_lib
 *                 (pack + CRC + LDPC + tone map) — invalid grammar is rejected.
 *   is_ft4      - 0 = FT8 (79 sym × 160 ms, BT 2.0), 1 = FT4 (105 × 48 ms, BT 1.0)
 *   audio_hz    - audio carrier of tone 0 (the operator's TX offset, e.g. 1500)
 *   sample_rate - output rate (e.g. 48000)
 *   out/max_out - caller-allocated float buffer. Required size is
 *                 n_sym * round(sample_rate * symbol_period); the C# caller
 *                 computes the identical formula.
 * Returns samples written (> 0), or:
 *   -1  message failed to encode (grammar/pack error)
 *   -2  bad arguments / buffer too small
 *   -3  out of memory
 * Full-scale ±1.0 sine; the caller applies its own headroom scaling.
 */
ZEUS_FT8_EXPORT int zeus_ft8_synth(const char* text, int is_ft4,
                                   float audio_hz, int sample_rate,
                                   float* out, int max_out);

#ifdef __cplusplus
}
#endif
#endif /* ZEUS_FT8_H */
