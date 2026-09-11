#!/bin/bash
# zeus-preflight.sh — Linux runtime-dependency check for the GUI (Photino) modes.
#
# Sourced by the desktop / server launchers (tarball) and by the AppImage AppRun.
# Photino's webview backend on Linux is WebKitGTK (libwebkit2gtk). Without it the
# native window cannot be created and Zeus exits immediately with no window — the
# same "flashes and closes, nothing happens" failure that a missing WebView2
# runtime causes on Windows. This is the Linux analogue of the Windows
# installer's WebView2/VC-redist checks: detect the dependency, offer to install
# it when we have a terminal, and otherwise guide the operator and fall back to
# browser (service) mode so Zeus is never left silently dead.
#
# All functions are best-effort and must never abort the launcher with set -e
# semantics — callers decide what to do with the return value.

# True (0) when libwebkit2gtk (4.1 or 4.0) is resolvable by the dynamic linker.
zeus_have_webkit() {
    if command -v ldconfig >/dev/null 2>&1; then
        ldconfig -p 2>/dev/null | grep -Eq 'libwebkit2gtk-4\.[01]' && return 0
    fi
    local d
    for d in /usr/lib /usr/lib64 /usr/local/lib \
             /usr/lib/x86_64-linux-gnu /usr/lib/aarch64-linux-gnu; do
        ls "${d}"/libwebkit2gtk-4.* >/dev/null 2>&1 && return 0
    done
    return 1
}

# Echo the distro-appropriate install command for WebKitGTK, or empty when the
# package manager isn't recognised. Package names track the per-distro names
# documented in the tarball/AppImage READMEs.
zeus_webkit_install_cmd() {
    if command -v apt-get >/dev/null 2>&1; then
        echo "sudo apt-get install -y libwebkit2gtk-4.1-0"
    elif command -v dnf >/dev/null 2>&1; then
        echo "sudo dnf install -y webkit2gtk4.1"
    elif command -v pacman >/dev/null 2>&1; then
        echo "sudo pacman -S --needed --noconfirm webkit2gtk-4.1"
    elif command -v zypper >/dev/null 2>&1; then
        echo "sudo zypper install -y libwebkit2gtk-4_1-0"
    else
        echo ""
    fi
}

# Best-effort desktop notification for the no-terminal (double-click) case.
zeus_notify() {
    local text="$1"
    if command -v zenity >/dev/null 2>&1; then
        zenity --warning --no-wrap --title="OpenHPSDR Zeus" --text="${text}" >/dev/null 2>&1 || true
    elif command -v kdialog >/dev/null 2>&1; then
        kdialog --title "OpenHPSDR Zeus" --sorry "${text}" >/dev/null 2>&1 || true
    elif command -v notify-send >/dev/null 2>&1; then
        notify-send "OpenHPSDR Zeus" "${text}" || true
    fi
}

# Ensure WebKitGTK is present for GUI (Photino) modes.
#   return 0 → proceed with the requested GUI mode
#   return 1 → caller should fall back to browser/service mode
# When run from a terminal, offers to install the dependency (real fulfilment);
# from a GUI launch it pops a dialog with the exact command. Either way it never
# leaves the operator with a silent dead launch.
zeus_ensure_webkit() {
    zeus_have_webkit && return 0

    local cmd msg ans
    cmd="$(zeus_webkit_install_cmd)"
    msg="OpenHPSDR Zeus needs the WebKitGTK library (libwebkit2gtk) for its native window, but it is not installed."

    if [ -t 0 ] && [ -t 1 ]; then
        echo "${msg}" >&2
        if [ -n "${cmd}" ]; then
            echo "" >&2
            echo "  Install command: ${cmd}" >&2
            printf 'Install it now? [Y/n] ' >&2
            read -r ans
            case "${ans}" in
                [Nn]*) ;;
                *) eval "${cmd}" || echo "Install failed — run the command above manually." >&2 ;;
            esac
        else
            echo "Could not detect your package manager — install libwebkit2gtk-4.1 with your distro's tools." >&2
        fi
        zeus_have_webkit && return 0
        echo "WebKitGTK still missing — falling back to browser (service) mode." >&2
        return 1
    fi

    # No controlling terminal (GUI double-click): we can't prompt or sudo, so
    # show the install command and fall back to the browser UI.
    local detail="${msg}"
    if [ -n "${cmd}" ]; then
        detail="${msg}

Install it from a terminal with:
    ${cmd}

Zeus will open in your web browser for now."
    fi
    zeus_notify "${detail}"
    return 1
}

# True (0) when the operator forces browser/service mode regardless of
# WebKitGTK availability: ZEUS_FORCE_BROWSER=1|true|yes. Escape hatch for
# platforms where WebKitGTK is installed but renders a blank (white) window —
# seen on Raspberry Pi OS Trixie (Wayland/labwc + V3D) with WebKitGTK 2.52.
zeus_browser_forced() {
    case "${ZEUS_FORCE_BROWSER:-}" in
        1|[Tt]rue|[Yy]es) return 0 ;;
        *) return 1 ;;
    esac
}

# Platform default for the native (Photino/WebKitGTK) window.
#   return 0 → attempt the native window
#   return 1 → default to the chromeless-browser (kiosk) UI instead
# On aarch64 the native window is DISABLED BY DEFAULT: field testing on a
# Saturn G2 (Raspberry Pi OS Trixie, Wayland/labwc, V3D, WebKitGTK 2.52)
# shows Photino painting a blank white window even with every known render
# workaround applied (DMA-BUF/compositing disables, Skia CPU rendering,
# XWayland). The browser fallback opens a chromeless Chromium --app window
# that is functionally identical, so kiosk-by-default is the working
# experience rather than a degraded one. Set ZEUS_FORCE_NATIVE=1 to opt back
# in (e.g. to re-test after a WebKitGTK upgrade, or on arm64 hardware with a
# different GPU stack).
zeus_native_window_viable() {
    case "${ZEUS_FORCE_NATIVE:-}" in
        1|[Tt]rue|[Yy]es) return 0 ;;
    esac
    [ "$(uname -m)" = "aarch64" ] && return 1
    return 0
}

# Export best-known WebKitGTK rendering workarounds for platforms where the
# GPU path is broken, BEFORE Photino creates its window. Everything here is
# export-if-unset so an operator's explicit setting always wins.
#
# Scope: aarch64 only. On the Pi's V3D/Wayland stack the accelerated WebKit
# paths are what paint the notorious blank-white window; on x86_64 desktops
# they work and disabling them would be a pointless performance regression.
#   - WEBKIT_DISABLE_DMABUF_RENDERER / WEBKIT_DISABLE_COMPOSITING_MODE:
#     the classic pre-Skia (< 2.52) switches; harmless no-ops on newer WebKit.
#   - WEBKIT_SKIA_ENABLE_CPU_RENDERING: the 2.52+/Skia equivalent.
#   - GDK_BACKEND=x11: only when a Wayland session also offers XWayland
#     (both WAYLAND_DISPLAY and DISPLAY set) — GTK/WebKit via XWayland is the
#     battle-tested path on Pi OS; never forced on pure-X or pure-Wayland
#     setups where it would be wrong or redundant.
zeus_export_webview_render_workarounds() {
    [ "$(uname -m)" = "aarch64" ] || return 0
    export WEBKIT_DISABLE_DMABUF_RENDERER="${WEBKIT_DISABLE_DMABUF_RENDERER:-1}"
    export WEBKIT_DISABLE_COMPOSITING_MODE="${WEBKIT_DISABLE_COMPOSITING_MODE:-1}"
    export WEBKIT_SKIA_ENABLE_CPU_RENDERING="${WEBKIT_SKIA_ENABLE_CPU_RENDERING:-1}"
    if [ -n "${WAYLAND_DISPLAY:-}" ] && [ -n "${DISPLAY:-}" ] && [ -z "${GDK_BACKEND:-}" ]; then
        export GDK_BACKEND=x11
    fi
}

# Wait (up to ~30 s) for the backend to answer on localhost:6060 before
# opening a browser at it, so the operator never lands on a connection-refused
# page during a slow cold start. Pure-bash /dev/tcp probe — no curl needed.
zeus_wait_for_backend() {
    local i
    for i in $(seq 1 60); do
        if (exec 3<>/dev/tcp/127.0.0.1/6060) 2>/dev/null; then
            exec 3>&- 3<&- 2>/dev/null
            return 0
        fi
        sleep 0.5
    done
    return 1   # not up yet (e.g. first-run FFTW wisdom bake) — open anyway
}

# Browser/service-mode fallback: run the headless backend and open a browser
# at the local URL, so a missing/broken GUI dependency still yields a working
# Zeus. Prefers a Chromium-family --app window (chromeless, looks and feels
# like the native Photino window and renders correctly on the Pi GPU stack
# where WebKitGTK does not); falls back to the default browser. Expects the
# current directory to contain ./OpenhpsdrZeus (the launchers cd there before
# sourcing this file). Passes through any extra args.
#
# PHOTINO PARITY: the kiosk window and the backend share one lifetime, both
# directions. The in-app Exit button (POST /api/app/quit) exits the backend ->
# we close the window; the operator closing the window -> we stop the backend.
# Without this the Exit button leaves a dead page on screen and looks broken.
# The dedicated --user-data-dir matters twice over: it forces Chromium to run
# as a process we own (otherwise --app hands off to any existing browser
# session and instantly exits, leaving us nothing to supervise or kill), and
# it keeps the kiosk window out of the operator's normal browser profile.
# It is PERSISTENT (under the Zeus data dir), not a throwaway: the frontend
# keeps per-display view state — panadapter dB window, zoom, layout — in the
# browser's localStorage, and a fresh profile per launch wiped it, forcing
# the operator to re-adjust scaling every session.

# ---- Twin guard (field incident 2026-09-10) ---------------------------------
# Exactly one Zeus may own :6060. A stale radio left behind by an update
# handoff (or a second launch) would otherwise (a) keep serving old code and
# (b) hold the kiosk browser profile — so OUR --app launch below hands off to
# ITS window and exits at once, which the lifetime coupling reads as "operator
# closed the window" and kills the brand-new backend two seconds into its
# life, exit 0, unit inactive, orphan untouched. That exact sequence was
# watched happen. So: before the backend starts, find who owns :6060; if it
# is a Zeus, terminate its children (p2app), the backend, its launcher shell
# (whose EXIT trap closes that instance's kiosk window) and its AppImage
# runtime; clear any kiosk window still holding our profile; wait for the
# port. A non-Zeus owner is logged and left alone. Needs iproute2's ss —
# skipped silently without it (the backend's own InstanceGuard still runs).
zeus_reclaim_twin() {
    command -v ss >/dev/null 2>&1 || return 0
    local port=6060 pids pid cmd ppid pcmd killed="" i
    pids=$(ss -ltnpH "sport = :${port}" 2>/dev/null | grep -o 'pid=[0-9]*' | cut -d= -f2 | sort -u)
    for pid in ${pids}; do
        [ "${pid}" = "$$" ] && continue
        cmd=$(tr '\0' ' ' < "/proc/${pid}/cmdline" 2>/dev/null)
        case "${cmd}" in
            *OpenhpsdrZeus*) ;;
            *)  echo "twin guard: :${port} owned by non-Zeus pid ${pid} (${cmd}) — leaving it alone" >&2
                continue ;;
        esac
        echo "twin guard: stale Zeus pid ${pid} owns :${port} — terminating it, its children and its launcher" >&2
        pkill -TERM -P "${pid}" 2>/dev/null || true
        ppid=$(sed 's/^[^)]*) //' "/proc/${pid}/stat" 2>/dev/null | awk '{print $2}')
        kill -TERM "${pid}" 2>/dev/null || true
        while [ -n "${ppid}" ] && [ "${ppid}" -gt 1 ] 2>/dev/null; do
            pcmd=$(tr '\0' ' ' < "/proc/${ppid}/cmdline" 2>/dev/null)
            case "${pcmd}" in
                *AppRun*|*OpenhpsdrZeus*|*zeus-preflight*)
                    kill -TERM "${ppid}" 2>/dev/null || true
                    ppid=$(sed 's/^[^)]*) //' "/proc/${ppid}/stat" 2>/dev/null | awk '{print $2}') ;;
                *) break ;;
            esac
        done
        killed="yes"
    done
    # A kiosk window still holding OUR profile belongs to a dead or dying
    # instance; clear it so our --app launch runs as a process we own.
    if pkill -TERM -f -- "--user-data-dir=${XDG_DATA_HOME:-$HOME/.local/share}/Zeus/kiosk-profile" 2>/dev/null; then
        killed="yes"
    fi
    if [ -n "${killed}" ]; then
        for i in $(seq 1 50); do
            if ! (exec 3<>/dev/tcp/127.0.0.1/${port}) 2>/dev/null; then
                echo "twin guard: :${port} reclaimed" >&2
                return 0
            fi
            exec 3>&- 3<&- 2>/dev/null
            sleep 0.1
        done
        echo "twin guard: :${port} still busy after 5 s — escalating to SIGKILL" >&2
        for pid in $(ss -ltnpH "sport = :${port}" 2>/dev/null | grep -o 'pid=[0-9]*' | cut -d= -f2 | sort -u); do
            kill -KILL "${pid}" 2>/dev/null || true
        done
    fi
    return 0
}

zeus_run_service_with_browser() {
    zeus_reclaim_twin
    echo "Starting OpenHPSDR Zeus in browser (service) mode on http://localhost:6060" >&2
    ./OpenhpsdrZeus "$@" &
    local backend_pid=$!
    local browser_pid=""
    local profile_dir=""
    zeus_kiosk_cleanup() {
        kill -TERM "${backend_pid}" ${browser_pid:+"${browser_pid}"} 2>/dev/null || true
    }
    trap zeus_kiosk_cleanup EXIT INT TERM
    zeus_wait_for_backend || true
    local url="http://localhost:6060"
    local app
    for app in chromium-browser chromium google-chrome-stable google-chrome; do
        if command -v "${app}" >/dev/null 2>&1; then
            profile_dir="${XDG_DATA_HOME:-$HOME/.local/share}/Zeus/kiosk-profile"
            mkdir -p "${profile_dir}"
            # Browser-level fullscreen is RETIRED (field report: with
            # --start-fullscreen the session covers the taskbar for its whole
            # life and no in-app control can exit it — the FULL SCR checkbox
            # governs ELEMENT fullscreen, a different mechanism the window
            # flag ignores). The launch is now maximized only: labwc keeps
            # the taskbar/desktop reachable above a maximized window, and the
            # app's own preferred-fullscreen (marker below still honored by
            # the frontend pref) re-enters element fullscreen on the first
            # tap — reversibly, so unchecking FULL SCR actually returns the
            # desktop. Cost: one tap at boot instead of zero; the price of a
            # fullscreen the operator can always leave.
            local fsflag=""
            # --start-maximized + explicit size: the throwaway profile means
            # Chromium cannot remember the window geometry between launches,
            # and a small default window trips the UI's responsive breakpoint
            # into the stacked mobile layout. Open big so the operator gets
            # the desktop layout (full panadapter) every time.
            #
            # The size must come from the PANEL, never a constant: the old
            # hardcoded 1600,900 decreed an oversize window on the G2's
            # 1280x800 — --start-fullscreen then fullscreened that decree,
            # and no amount of in-page fullscreen logic could shrink a
            # window whose size was fixed on its own command line (the
            # six-commit fullscreen saga, closed here). Detect the current
            # output mode; fall back to 1280x800 — desktop-layout wide,
            # and never larger than any panel this ships on.
            # TRANSFORM-AWARE (field report: window opened 800x1280 on the
            # 1280x800 panel): many small panels are PORTRAIT-NATIVE — the
            # hardware mode is 800x1280 and the compositor rotates it to
            # landscape with a 90-degree transform. wlr-randr's mode line
            # reports the NATIVE mode, not the logical size, so a
            # transform-blind read sizes the window sideways. Read the
            # transform and swap axes for 90/270. The xrandr fallback reads
            # the connected header's logical geometry, which already
            # includes rotation.
            local kiosk_w=1280 kiosk_h=800 kiosk_mode="" kiosk_xform=""
            if command -v wlr-randr >/dev/null 2>&1; then
                kiosk_mode=$(wlr-randr 2>/dev/null | awk '/current/{print $1; exit}')
                kiosk_xform=$(wlr-randr 2>/dev/null | awk '/Transform:/{print $2; exit}')
            fi
            if [ -z "${kiosk_mode}" ] && command -v xrandr >/dev/null 2>&1; then
                kiosk_mode=$(xrandr 2>/dev/null | awk '/ connected/{for(i=1;i<=NF;i++) if ($i ~ /^[0-9]+x[0-9]+\+/){split($i,a,"+"); print a[1]; exit}}')
            fi
            case "${kiosk_mode}" in
                [0-9]*x[0-9]*)
                    kiosk_w=${kiosk_mode%x*}
                    kiosk_h=${kiosk_mode#*x}
                    ;;
            esac
            case "${kiosk_xform}" in
                90|270|flipped-90|flipped-270)
                    local kiosk_tmp=${kiosk_w}
                    kiosk_w=${kiosk_h}
                    kiosk_h=${kiosk_tmp}
                    ;;
            esac
            # A browser that exits within seconds of launch while the backend
            # is healthy did not get closed by anyone — Chromium found another
            # window already holding our profile and handed off to it. Treat
            # that as the stale-window case: clear the squatter and relaunch
            # once. Only a browser that lived past that window counts as a
            # deliberate close below. (Field incident 2026-09-10: this handoff
            # killed a freshly started backend two seconds into its life.)
            local browser_attempt=0 browser_started
            while :; do
                browser_started=${SECONDS}
                "${app}" --app="${url}" --user-data-dir="${profile_dir}" \
                    ${fsflag} --start-maximized --window-size="${kiosk_w},${kiosk_h}" \
                    --no-first-run --no-default-browser-check >/dev/null 2>&1 &
                browser_pid=$!
                # First one out (backend Exit button, or operator closing the
                # window) takes the other with it. wait -n is bash >= 4.3 --
                # everywhere we ship; fall back to backend-only wait if absent.
                wait -n "${backend_pid}" "${browser_pid}" 2>/dev/null \
                    || wait "${backend_pid}"
                if [ "${browser_attempt}" -eq 0 ] \
                   && kill -0 "${backend_pid}" 2>/dev/null \
                   && ! kill -0 "${browser_pid}" 2>/dev/null \
                   && [ $((SECONDS - browser_started)) -lt 3 ]; then
                    browser_attempt=1
                    echo "kiosk: browser exited within 3 s of launch — handed off to a stale window; clearing it and relaunching once" >&2
                    pkill -TERM -f -- "--user-data-dir=${profile_dir}" 2>/dev/null || true
                    sleep 1
                    continue
                fi
                break
            done
            # Who went first decides the verdict. BACKEND first: collect its
            # real exit status (bash keeps it for a reaped child) and return
            # it — a segfaulting backend must NOT leave here as success, or
            # systemd's Restart=on-failure never fires and the radio strands
            # on a blank screen (field incident, v1.21 first boot). BROWSER
            # first (operator closed the window): give the page's
            # beforeunload layout beacon a moment to land before we take the
            # backend down — killing it instantly loses whatever the operator
            # arranged in the final save-debounce window — and return 0; a
            # deliberate close is a clean exit however the backend dies to
            # our TERM.
            local backend_status=0
            if ! kill -0 "${backend_pid}" 2>/dev/null; then
                wait "${backend_pid}"
                backend_status=$?
            else
                sleep 1.5
            fi
            zeus_kiosk_cleanup
            trap - EXIT INT TERM
            wait 2>/dev/null || true
            return "${backend_status}"
        fi
    done
    local opener
    for opener in xdg-open gnome-open kde-open; do
        if command -v "${opener}" >/dev/null 2>&1; then
            "${opener}" "${url}" >/dev/null 2>&1 &
            break
        fi
    done
    zeus_notify "OpenHPSDR Zeus is running in your web browser at ${url}"
    wait "${backend_pid}"
}
