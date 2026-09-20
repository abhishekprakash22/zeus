/* SPDX-License-Identifier: GPL-2.0-or-later
 *
 * Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
 * Copyright (C) 2025-2026 Brian Keating (EI6LF),
 *                         Douglas J. Cerrato (KB2UKA),
 *                         Christian Suarez (N9WAR),
 *                         Ramón Martínez (EA5IUE), and contributors.
 */
/* Harness for the zeus_ft8 callsign hash table.
 *
 * Includes the shim source directly so the static table functions are
 * reachable: the bugs were (a) the table being wiped on every decode call and
 * (b) the lookup comparing the wrong bits of the stored 22-bit hash. */
#include "zeus_ft8.c"

#include <stdio.h>
#include "ft8/text.h"

static int failures = 0;

static void check(int ok, const char* what)
{
    printf("%-58s %s\n", what, ok ? "ok" : "FAIL");
    if (!ok) failures++;
}

/* ft8_lib's own hash, copied from save_callsign() in ft8/message.c. */
static uint32_t n22_of(const char* callsign)
{
    uint64_t n58 = 0;
    int i = 0;
    while (callsign[i] != '\0' && i < 11)
    {
        int j = nchar(callsign[i], FT8_CHAR_TABLE_ALPHANUM_SPACE_SLASH);
        if (j < 0) return 0;
        n58 = (38 * n58) + j;
        i++;
    }
    while (i < 11) { n58 = (38 * n58); i++; }
    return (uint32_t)(((47055833459ull * n58) >> (64 - 22)) & 0x3FFFFFul);
}

static void expect_resolves(const char* call)
{
    uint32_t n22 = n22_of(call);
    char out[12];
    char what[128];

    snprintf(what, sizeof what, "%s resolves from its 22-bit hash", call);
    check(hashtable_lookup(FTX_CALLSIGN_HASH_22_BITS, n22, out) &&
          strcmp(out, call) == 0, what);

    snprintf(what, sizeof what, "%s resolves from its 12-bit hash", call);
    check(hashtable_lookup(FTX_CALLSIGN_HASH_12_BITS, n22 >> 10, out) &&
          strcmp(out, call) == 0, what);

    snprintf(what, sizeof what, "%s resolves from its 10-bit hash", call);
    check(hashtable_lookup(FTX_CALLSIGN_HASH_10_BITS, n22 >> 12, out) &&
          strcmp(out, call) == 0, what);
}

int main(void)
{
    const char* calls[] = { "II7ABB", "EA5IUE/P", "MW0USK", "VP2V/K1ABC", "EA5IUE" };
    const int n_calls = (int)(sizeof calls / sizeof calls[0]);

    /* A callsign heard once must be resolvable at all three hash widths. */
    for (int i = 0; i < n_calls; i++)
        hashtable_add(calls[i], n22_of(calls[i]));
    for (int i = 0; i < n_calls; i++)
        expect_resolves(calls[i]);

    /* An unknown hash must NOT resolve — and must not hand back an empty
     * string as if it had. */
    char out[12];
    check(!hashtable_lookup(FTX_CALLSIGN_HASH_22_BITS, n22_of("ZZ9ZZZ"), out),
          "an unheard callsign does not resolve");

    /* Hash 0 must not "match" an empty slot. */
    check(!hashtable_lookup(FTX_CALLSIGN_HASH_22_BITS, 0, out) || out[0] != '\0',
          "hash 0 never resolves to an empty callsign");

    /* Re-adding is idempotent, and the callsign still resolves. */
    hashtable_add("II7ABB", n22_of("II7ABB"));
    expect_resolves("II7ABB");

    /* Overfill the table: it must keep learning, and the newest callsign must
     * still be findable (the old code silently dropped it). */
    char filler[12];
    for (int i = 0; i < CALLSIGN_HASHTABLE_SIZE * 2; i++)
    {
        snprintf(filler, sizeof filler, "F%dABC", i);
        hashtable_add(filler, n22_of(filler));
    }
    snprintf(filler, sizeof filler, "F%dABC", CALLSIGN_HASHTABLE_SIZE * 2 - 1);
    expect_resolves(filler);

    printf("\n%s (%d failure%s)\n", failures ? "FAILED" : "PASSED",
           failures, failures == 1 ? "" : "s");
    return failures ? 1 : 0;
}
