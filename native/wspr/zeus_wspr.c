/* SPDX-License-Identifier: GPL-2.0-or-later
 *
 * zeus_wspr — thin flat-ABI shim over the vendored K9AN wsprd decoder and
 * the wsprsim channel-symbol encoder (see README.md / LICENSE.wsprd for
 * provenance). Mirrors native/ft8/zeus_ft8.c: one .so, C ABI only, no
 * global state leaking to the caller beyond wsprd's own internals.
 *
 * Decoder input: 120 s of complex baseband at 375 Hz (45000 samples),
 * centred so that the classic 1500 Hz WSPR audio window sits at 0 Hz.
 * The C# side owns the 48 kHz -> 375 Hz IQ conversion.
 *
 * The encoder wraps get_wspr_channel_symbols(): "CALL GRID DBM" -> 162
 * channel symbols (0..3, sync folded in). Used by the beacon keyer so the
 * bit-exact WSJT-X packing/convolution/interleave lives in C, not in a
 * hand-transcribed C# port.
 */

#include <string.h>
#include <stdlib.h>
#include "wsprd.h"
#include "wsprsim_utils.h"

#define ZEUS_WSPR_ABI 1

typedef struct {
    double freq_hz;      /* absolute spot frequency */
    float  snr_db;
    float  dt_sec;
    float  drift_hz;
    char   message[24];  /* "CALL GRID DBM" as decoded */
} zeus_wspr_spot;

/* wsprd's callsign-hash tables — decoder writes <...> hashes here across
 * calls; the encoder's self-check unpack reads them. Zero-initialised is a
 * valid empty state. */
static char hashtab[HASHTAB_SIZE * HASHTAB_ENTRY_LEN];
static char loctab[HASHTAB_SIZE * LOCTAB_ENTRY_LEN];

int zeus_wspr_abi_version(void) { return ZEUS_WSPR_ABI; }

/* Decode one 120 s slot. idat/qdat: 375 Hz baseband, `samples` each
 * (45000 for a full slot). dial_freq_hz: dial for absolute spot freqs.
 * Returns the number of spots written to out (<= max_out), or -1. */
int zeus_wspr_decode(const float *idat, const float *qdat, int samples,
                     int dial_freq_hz, zeus_wspr_spot *out, int max_out)
{
    if (!idat || !qdat || samples <= 0 || !out || max_out <= 0) return -1;

    static struct decoder_results results[MAX_UNIQUES];
    struct decoder_options opt;
    memset(&opt, 0, sizeof opt);
    memset(results, 0, sizeof results);
    opt.freq         = dial_freq_hz;
    opt.quickmode    = 0;
    opt.usehashtable = 0;   /* type-3 hash spots off until surfaced in UI */
    opt.npasses      = 2;
    opt.subtraction  = 1;

    int n = 0;
    /* wsprd mutates the buffers during subtraction passes — copy. */
    float *ibuf = malloc(sizeof(float) * (size_t)samples);
    float *qbuf = malloc(sizeof(float) * (size_t)samples);
    if (!ibuf || !qbuf) { free(ibuf); free(qbuf); return -1; }
    memcpy(ibuf, idat, sizeof(float) * (size_t)samples);
    memcpy(qbuf, qdat, sizeof(float) * (size_t)samples);

    int ok = wspr_decode(ibuf, qbuf, samples, opt, results, &n);
    free(ibuf); free(qbuf);
    if (!ok && n <= 0) return 0;

    if (n > max_out) n = max_out;
    if (n > MAX_UNIQUES) n = MAX_UNIQUES;
    for (int i = 0; i < n; i++) {
        out[i].freq_hz  = results[i].freq;
        out[i].snr_db   = results[i].snr;
        out[i].dt_sec   = results[i].dt;
        out[i].drift_hz = results[i].drift;
        memset(out[i].message, 0, sizeof out[i].message);
        strncpy(out[i].message, results[i].message, sizeof out[i].message - 1);
    }
    return n;
}

/* "CALL GRID DBM" -> 162 channel symbols (0..3). Returns 0 on success.
 * (get_wspr_channel_symbols returns 1 on success, 0 on internal failure.) */
int zeus_wspr_encode(const char *message, unsigned char symbols[162])
{
    if (!message || !symbols) return -1;
    char msg[64];
    strncpy(msg, message, sizeof msg - 1);
    msg[sizeof msg - 1] = 0;
    return get_wspr_channel_symbols(msg, hashtab, loctab, symbols) == 1 ? 0 : -1;
}
