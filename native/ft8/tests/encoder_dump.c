/* SPDX-License-Identifier: GPL-2.0-or-later
 *
 * encoder_dump — the native oracle for the managed FT8/FT4 encoder
 * (docs/designs/ft8-managed-port.md). Reads one message per line on stdin
 * and prints, tab-separated:
 *
 *   message  rc  payload(20 hex)  ft8 tones(79 digits)  ft4 tones(105 digits)
 *
 * The payload and tones are "-" when rc != 0. Built from the same vendored
 * ft8_lib sources as libzeus_ft8, with a hash interface that stores nothing:
 * encoding never looks a callsign up, and n12 is computed directly.
 *
 *   cc -O1 -I. tests/encoder_dump.c ft8/message.c ft8/text.c ft8/crc.c \
 *      ft8/encode.c ft8/constants.c -o /tmp/encoder_dump
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "ft8/constants.h"
#include "ft8/encode.h"
#include "ft8/message.h"

static bool no_lookup(ftx_callsign_hash_type_t t, uint32_t h, char* c)
{
    (void)t; (void)h; c[0] = '\0';
    return false;
}
static void no_save(const char* c, uint32_t h) { (void)c; (void)h; }
static ftx_callsign_hash_interface_t hash_if = { no_lookup, no_save };

static uint32_t rng_state = 2463534242u;
static uint32_t xorshift32(void)
{
    uint32_t x = rng_state;
    x ^= x << 13; x ^= x >> 17; x ^= x << 5;
    return rng_state = x;
}

static int dump_random(int n)
{
    for (int k = 0; k < n; k++)
    {
        ftx_message_t msg;
        ftx_message_init(&msg);
        for (int i = 0; i < FTX_PAYLOAD_LENGTH_BYTES; i++) msg.payload[i] = (uint8_t)xorshift32();
        msg.payload[9] &= 0xF8u;                  /* 77 bits: the low 3 are not payload */
        /* Bias i3 towards the types ft8_lib decodes (0, 1, 2, 4). */
        static const uint8_t i3s[8] = { 0, 1, 2, 4, 0, 1, 4, 3 };
        msg.payload[9] = (uint8_t)((msg.payload[9] & 0xC0u) | (i3s[k & 7] << 3));
        if ((k & 15) == 0) msg.payload[8] &= 0xFEu, msg.payload[9] &= 0x3Fu;   /* some 0.0 free text */

        char text[FTX_MAX_MESSAGE_LENGTH];
        ftx_message_offsets_t offsets;
        ftx_message_rc_t rc = ftx_message_decode(&msg, &hash_if, text, &offsets);
        for (int i = 0; i < FTX_PAYLOAD_LENGTH_BYTES; i++) printf("%02x", msg.payload[i]);
        printf("\t%d\t%s\n", (int)rc, rc == FTX_MESSAGE_RC_OK ? text : "-");
    }
    return 0;
}

int main(int argc, char** argv)
{
    if (argc == 3 && strcmp(argv[1], "--random") == 0) return dump_random(atoi(argv[2]));

    char line[256];
    while (fgets(line, sizeof line, stdin))
    {
        line[strcspn(line, "\r\n")] = '\0';
        ftx_message_t msg;
        ftx_message_init(&msg);
        ftx_message_rc_t rc = ftx_message_encode(&msg, &hash_if, line);
        printf("%s\t%d\t", line, (int)rc);
        if (rc != FTX_MESSAGE_RC_OK) { printf("-\t-\t-\n"); continue; }

        for (int i = 0; i < FTX_PAYLOAD_LENGTH_BYTES; i++) printf("%02x", msg.payload[i]);
        uint8_t t8[FT8_NN], t4[FT4_NN];
        ft8_encode(msg.payload, t8);
        ft4_encode(msg.payload, t4);
        printf("\t");
        for (int i = 0; i < FT8_NN; i++) printf("%d", t8[i]);
        printf("\t");
        for (int i = 0; i < FT4_NN; i++) printf("%d", t4[i]);
        printf("\n");
    }
    return 0;
}
