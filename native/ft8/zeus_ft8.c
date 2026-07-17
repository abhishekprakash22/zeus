/* SPDX-License-Identifier: GPL-2.0-or-later
 *
 * zeus_ft8 — a flat, P/Invoke-friendly FT8/FT4 decode + synth API over ft8_lib.
 *
 * ft8_lib (Karlis Goba, MIT) exposes a low-level monitor/candidate/decode API.
 * Marshalling that from C# would mean projecting several structs and owning
 * their lifetimes across the boundary. Instead this shim keeps ALL of it on the
 * C side and hands back a plain array of PODs — one call in, N decodes out.
 *
 * Modelled on ft8_lib's demo/decode_ft8.c; the synth side is modelled on
 * demo/gen_ft8.c (gfsk_pulse / synth_gfsk), reimplemented here because the
 * demo sources are not vendored.
 *
 * Threading: zeus_ft8_decode() and zeus_ft8_synth() are self-contained —
 * they allocate what they need per call and free it before returning. The
 * caller invokes them from worker threads, never the audio thread. NOTE the
 * callsign hashtable is shared static state (as upstream); decode resets it
 * per call, and synth only APPENDS via save_hash for nonstandard calls, so
 * interleaving is benign for standard traffic.
 */

#include <stdlib.h>
#include <string.h>
#include <math.h>

#include "ft8/decode.h"
#include "ft8/encode.h"
#include "ft8/message.h"
#include "ft8/constants.h"
#include "common/monitor.h"

#include "zeus_ft8.h"

#define ZEUS_MAX_CANDIDATES 140
#define ZEUS_LDPC_ITERS     20
#define ZEUS_MIN_SCORE      10

/* ft8_lib works at 12 kHz. */
#define ZEUS_FT8_RATE 12000

/* --- callsign hash table -------------------------------------------------
 * ft8_lib needs somewhere to remember callsigns so it can resolve the hashed
 * <...> forms used by compound/portable calls. The demo uses a fixed table; we
 * do the same, reset per call.
 */
#define CALLSIGN_HASHTABLE_SIZE 256

static struct
{
    char callsign[12];
    uint32_t hash;
} hashtable[CALLSIGN_HASHTABLE_SIZE];

static int hashtable_size = 0;

static void hashtable_init(void)
{
    memset(hashtable, 0, sizeof(hashtable));
    hashtable_size = 0;
}

static void hashtable_add(const char* callsign, uint32_t hash)
{
    uint16_t hash10 = (hash >> 12) & 0x3FFu;
    int idx = hash10 % CALLSIGN_HASHTABLE_SIZE;
    for (int i = 0; i < CALLSIGN_HASHTABLE_SIZE; i++)
    {
        if (hashtable[idx].callsign[0] == '\0')
        {
            strncpy(hashtable[idx].callsign, callsign, 11);
            hashtable[idx].callsign[11] = '\0';
            hashtable[idx].hash = hash;
            hashtable_size++;
            return;
        }
        if (hashtable[idx].hash == hash &&
            strcmp(hashtable[idx].callsign, callsign) == 0)
            return;                                   /* already known */
        idx = (idx + 1) % CALLSIGN_HASHTABLE_SIZE;
    }
}

static bool hashtable_lookup(ftx_callsign_hash_type_t hash_type,
                             uint32_t hash, char* callsign)
{
    uint8_t hash_shift = (hash_type == FTX_CALLSIGN_HASH_10_BITS) ? 12
                       : (hash_type == FTX_CALLSIGN_HASH_12_BITS) ? 10 : 0;
    uint16_t hash10 = (hash >> (12 - hash_shift)) & 0x3FFu;
    int idx = hash10 % CALLSIGN_HASHTABLE_SIZE;
    for (int i = 0; i < CALLSIGN_HASHTABLE_SIZE; i++)
    {
        if ((hashtable[idx].hash & ((1u << (22 - hash_shift)) - 1u)) ==
            (hash & ((1u << (22 - hash_shift)) - 1u)))
        {
            strcpy(callsign, hashtable[idx].callsign);
            return true;
        }
        if (hashtable[idx].callsign[0] == '\0') break;
        idx = (idx + 1) % CALLSIGN_HASHTABLE_SIZE;
    }
    callsign[0] = '\0';
    return false;
}

static ftx_callsign_hash_interface_t hash_if = {
    .lookup_hash = hashtable_lookup,
    .save_hash   = hashtable_add,
};

/* --- resampling ----------------------------------------------------------
 * The RX tap delivers whatever rate the pipeline runs at (typically 48 kHz).
 * ft8_lib wants 12 kHz. Linear interpolation is adequate: FT8 tones are
 * ~6.25 Hz apart and the decoder does its own FFT — this is not the place to
 * be clever, and a cheap resample keeps the Pi's decode budget intact.
 */
static float* resample_to_12k(const float* in, int n_in, int rate_in, int* n_out)
{
    if (rate_in == ZEUS_FT8_RATE)
    {
        float* out = (float*)malloc((size_t)n_in * sizeof(float));
        if (!out) return NULL;
        memcpy(out, in, (size_t)n_in * sizeof(float));
        *n_out = n_in;
        return out;
    }

    double ratio = (double)ZEUS_FT8_RATE / (double)rate_in;
    int n = (int)((double)n_in * ratio);
    if (n <= 0) { *n_out = 0; return NULL; }

    float* out = (float*)malloc((size_t)n * sizeof(float));
    if (!out) return NULL;

    for (int i = 0; i < n; i++)
    {
        double src = (double)i / ratio;
        int i0 = (int)src;
        int i1 = i0 + 1;
        if (i1 >= n_in) i1 = n_in - 1;
        double frac = src - (double)i0;
        out[i] = (float)((1.0 - frac) * in[i0] + frac * in[i1]);
    }
    *n_out = n;
    return out;
}

int zeus_ft8_version(void) { return 1; }

int zeus_ft8_decode(const float* audio, int n_samples, int sample_rate,
                    int is_ft4, zeus_ft8_decode_t* out, int max_out)
{
    if (!audio || n_samples <= 0 || !out || max_out <= 0) return 0;

    int n12 = 0;
    float* sig = resample_to_12k(audio, n_samples, sample_rate, &n12);
    if (!sig || n12 <= 0) { free(sig); return 0; }

    ftx_protocol_t proto = is_ft4 ? FTX_PROTOCOL_FT4 : FTX_PROTOCOL_FT8;
    float slot_time = is_ft4 ? FT4_SLOT_TIME : FT8_SLOT_TIME;

    monitor_config_t cfg = {
        .f_min = 100,
        .f_max = 3000,
        .sample_rate = ZEUS_FT8_RATE,
        .time_osr = 2,
        .freq_osr = 2,
        .protocol = proto,
    };

    monitor_t mon;
    monitor_init(&mon, &cfg);
    hashtable_init();

    /* Feed the slot through the monitor one block at a time. */
    int frame_pos = 0;
    while (frame_pos + mon.block_size <= n12)
    {
        monitor_process(&mon, sig + frame_pos);
        frame_pos += mon.block_size;
        if (mon.wf.num_blocks >= mon.wf.max_blocks) break;
    }

    ftx_candidate_t candidates[ZEUS_MAX_CANDIDATES];
    int n_cand = ftx_find_candidates(&mon.wf, ZEUS_MAX_CANDIDATES,
                                     candidates, ZEUS_MIN_SCORE);

    int n_out = 0;
    for (int i = 0; i < n_cand && n_out < max_out; i++)
    {
        const ftx_candidate_t* cand = &candidates[i];

        ftx_message_t message;
        ftx_decode_status_t status;
        if (!ftx_decode_candidate(&mon.wf, cand, ZEUS_LDPC_ITERS, &message, &status))
            continue;

        /* ftx_message_decode takes an offsets out-param and dereferences it
           UNCONDITIONALLY (ft8/message.c:401) — passing NULL segfaults. Supply a
           real struct even though we don't render field highlights. Buffer must
           be FTX_MAX_MESSAGE_LENGTH, as in demo/decode_ft8.c. */
        char text[FTX_MAX_MESSAGE_LENGTH];
        ftx_message_offsets_t offsets;
        ftx_message_rc_t rc = ftx_message_decode(&message, &hash_if, text, &offsets);
        if (rc != FTX_MESSAGE_RC_OK) continue;

        float freq_hz = (mon.min_bin + cand->freq_offset +
                         (float)cand->freq_sub / mon.wf.freq_osr) / mon.symbol_period;
        float time_sec = (cand->time_offset +
                          (float)cand->time_sub / mon.wf.time_osr) * mon.symbol_period;

        /* De-duplicate: the same message often wins several candidates. */
        int dup = 0;
        for (int j = 0; j < n_out; j++)
        {
            if (strcmp(out[j].text, text) == 0 &&
                fabsf(out[j].freq_hz - freq_hz) < 5.0f) { dup = 1; break; }
        }
        if (dup) continue;

        zeus_ft8_decode_t* d = &out[n_out++];
        /* ft8_lib's own approximation (demo/decode_ft8.c). */
        d->snr_db  = (int)(cand->score * 0.5f);
        d->dt_sec  = time_sec;
        d->freq_hz = freq_hz;
        d->score   = cand->score;
        strncpy(d->text, text, sizeof(d->text) - 1);
        d->text[sizeof(d->text) - 1] = '\0';
    }

    monitor_free(&mon);
    free(sig);
    (void)slot_time;
    return n_out;
}

/* =========================================================================
 * SYNTH — text → GFSK waveform. The TX counterpart of the decode above:
 * ftx_message_encode packs the text (CRC + LDPC land inside ft8_encode /
 * ft4_encode's tone mapping), then the standard WSJT-X GFSK pulse-shaped
 * synthesis produces the audio. Reimplements demo/gen_ft8.c's gfsk_pulse /
 * synth_gfsk, which are not vendored.
 * ========================================================================= */

/* π·sqrt(2/ln 2) — the Gaussian pulse constant (WSJT-X / ft8_lib gen). */
#define GFSK_CONST_K 5.336446f

/* Symbol BT products (gen_ft8.c/gen_ft4.c; not in constants.h). */
#ifndef FT8_SYMBOL_BT
#define FT8_SYMBOL_BT 2.0f
#endif
#ifndef FT4_SYMBOL_BT
#define FT4_SYMBOL_BT 1.0f
#endif

/* Gaussian-smoothed frequency pulse, 3 symbols long. */
static void gfsk_pulse(int n_spsym, float symbol_bt, float* pulse)
{
    for (int i = 0; i < 3 * n_spsym; i++)
    {
        float t = i / (float)n_spsym - 1.5f;
        float arg1 = GFSK_CONST_K * symbol_bt * (t + 0.5f);
        float arg2 = GFSK_CONST_K * symbol_bt * (t - 0.5f);
        pulse[i] = (erff(arg1) - erff(arg2)) / 2.0f;
    }
}

/* Continuous-phase GFSK synthesis (WSJT-X algorithm as in ft8_lib's demo):
 * per-sample phase increments from the tone sequence convolved with the
 * Gaussian pulse, dummy symbols flattening the edge tails, and a raised-cosine
 * amplitude ramp over the first/last n_spsym/8 samples. */
static int synth_gfsk(const uint8_t* symbols, int n_sym, float f0,
                      float symbol_bt, float symbol_period, int rate,
                      float* signal)
{
    int n_spsym = (int)(0.5f + rate * symbol_period);
    if (n_spsym <= 0) return -2;
    int n_wave = n_sym * n_spsym;

    float* dphi  = (float*)malloc(sizeof(float) * (size_t)(n_wave + 2 * n_spsym));
    float* pulse = (float*)malloc(sizeof(float) * (size_t)(3 * n_spsym));
    if (!dphi || !pulse) { free(dphi); free(pulse); return -3; }

    gfsk_pulse(n_spsym, symbol_bt, pulse);

    const float hmod = 1.0f;
    float dphi_peak = 2.0f * (float)M_PI * hmod / (float)n_spsym;
    float dphi_base = 2.0f * (float)M_PI * f0 / (float)rate;
    for (int i = 0; i < n_wave + 2 * n_spsym; i++) dphi[i] = dphi_base;

    for (int i = 0; i < n_sym; i++)
    {
        int ib = i * n_spsym;
        for (int j = 0; j < 3 * n_spsym; j++)
            dphi[ib + j] += dphi_peak * (float)symbols[i] * pulse[j];
    }
    /* Dummy symbols before and after the frame (same tone as the first/last
       real symbol) flatten the pulse tails so the edges stay on-frequency. */
    for (int j = 0; j < 2 * n_spsym; j++)
    {
        dphi[j]          += dphi_peak * pulse[j + n_spsym] * (float)symbols[0];
        dphi[n_wave + j] += dphi_peak * pulse[j] * (float)symbols[n_sym - 1];
    }

    float phi = 0.0f;
    for (int k = 0; k < n_wave; k++)
    {
        signal[k] = sinf(phi);
        phi = fmodf(phi + dphi[k + n_spsym], 2.0f * (float)M_PI);
    }

    /* Raised-cosine key clicks suppression. */
    int n_ramp = n_spsym / 8;
    for (int i = 0; i < n_ramp; i++)
    {
        float env = (1.0f - cosf(2.0f * (float)M_PI * (float)i / (2.0f * (float)n_ramp))) / 2.0f;
        signal[i] *= env;
        signal[n_wave - 1 - i] *= env;
    }

    free(dphi);
    free(pulse);
    return n_wave;
}

int zeus_ft8_synth(const char* text, int is_ft4, float audio_hz,
                   int sample_rate, float* out, int max_out)
{
    if (!text || !out || sample_rate <= 0 || max_out <= 0) return -2;

    ftx_message_t msg;
    ftx_message_rc_t rc = ftx_message_encode(&msg, &hash_if, text);
    if (rc != FTX_MESSAGE_RC_OK) return -1;

    uint8_t tones[FT4_NN > FT8_NN ? FT4_NN : FT8_NN];
    int n_sym;
    float period, bt;
    if (is_ft4)
    {
        ft4_encode(msg.payload, tones);
        n_sym = FT4_NN;
        period = FT4_SYMBOL_PERIOD;
        bt = FT4_SYMBOL_BT;
    }
    else
    {
        ft8_encode(msg.payload, tones);
        n_sym = FT8_NN;
        period = FT8_SYMBOL_PERIOD;
        bt = FT8_SYMBOL_BT;
    }

    int n_spsym = (int)(0.5f + sample_rate * period);
    if (n_sym * n_spsym > max_out) return -2;

    return synth_gfsk(tones, n_sym, audio_hz, bt, period, sample_rate, out);
}
