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
zeus_run_service_with_browser() {
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
            # Fullscreen restore: the frontend cannot re-enter the Fullscreen
            # API at load (gesture-gated), but WE control the launch flags.
            # The backend drops/removes this marker on every operator toggle
            # (POST /api/ui/kiosk-fullscreen), so the kiosk comes back exactly
            # as it was left — zero gestures. --start-fullscreen wins over
            # --start-maximized in Chromium; both stay so exiting fullscreen
            # lands on a maximized window, not a tiny default one.
            local fsflag=""
            [ -f "${XDG_DATA_HOME:-$HOME/.local/share}/Zeus/kiosk-fullscreen" ] \
                && fsflag="--start-fullscreen"
            # --start-maximized + explicit size: the throwaway profile means
            # Chromium cannot remember the window geometry between launches,
            # and a small default window trips the UI's responsive breakpoint
            # into the stacked mobile layout. Open big so the operator gets
            # the desktop layout (full panadapter) every time.
            "${app}" --app="${url}" --user-data-dir="${profile_dir}" \
                ${fsflag} --start-maximized --window-size=1600,900 \
                --no-first-run --no-default-browser-check >/dev/null 2>&1 &
            browser_pid=$!
            # First one out (backend Exit button, or operator closing the
            # window) takes the other with it. wait -n is bash >= 4.3 --
            # everywhere we ship; fall back to backend-only wait if absent.
            wait -n "${backend_pid}" "${browser_pid}" 2>/dev/null \
                || wait "${backend_pid}"
            # If the BROWSER went first (operator closed the window), give the
            # page's beforeunload layout beacon a moment to land before we
            # take the backend down — killing it instantly loses whatever the
            # operator arranged in the final save-debounce window.
            if kill -0 "${backend_pid}" 2>/dev/null; then
                sleep 1.5
            fi
            zeus_kiosk_cleanup
            trap - EXIT INT TERM
            wait 2>/dev/null || true
            return
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
