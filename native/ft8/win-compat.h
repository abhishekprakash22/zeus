/* SPDX-License-Identifier: GPL-2.0-or-later
 *
 * Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
 * Copyright (C) 2025-2026 Brian Keating (EI6LF),
 *                         Douglas J. Cerrato (KB2UKA),
 *                         Christian Suarez (N9WAR),
 *                         Ramón Martínez (EA5IUE), and contributors.
 *
 * The one POSIX function the vendored ft8_lib uses that Windows does not
 * have. ft8/message.c calls stpcpy() once, in unpackgrid(); without this the
 * Windows build stops there.
 *
 * Force-included (-include win-compat.h) rather than patched into the vendored
 * source, so ft8_lib stays byte-identical to upstream and re-vendoring it does
 * not silently drop the shim.
 */
#ifndef ZEUS_FT8_WIN_COMPAT_H
#define ZEUS_FT8_WIN_COMPAT_H

#include <string.h>

static inline char* stpcpy(char* dst, const char* src)
{
    size_t n = strlen(src);
    memcpy(dst, src, n + 1);   /* copy the terminator too */
    return dst + n;            /* POSIX: end of the copy, not its start */
}

#endif /* ZEUS_FT8_WIN_COMPAT_H */
