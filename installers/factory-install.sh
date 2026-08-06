#!/bin/sh
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
# Copyright (C) 2025-2026 Brian Keating (EI6LF),
#                         Douglas J. Cerrato (KB2UKA), and contributors.
#
# factory-install.sh — provision Zeus on a fresh OS image the ONE correct way.
#
#   sudo -u <operator> ./factory-install.sh                 # fetch latest via manifest
#   sudo -u <operator> ./factory-install.sh /path/to/Zeus.AppImage
#   ZEUS_MANIFEST_URL=https://.../latest.json ./factory-install.sh
#
# What "correct" means (each rule bought with a field incident):
#   * The image lives at a STABLE, VERSIONLESS path in a boring directory
#     (~/Applications/OpenhpsdrZeus.AppImage) — never the Desktop, never a
#     version-named file. Path stability is the self-updater's core
#     assumption; the Desktop gets a shortcut, not the binary.
#   * The AppImage-path memory is pre-seeded, so the very first boot is a
#     fully-armed self-updating install (no "launch it once" prerequisite).
#   * A managed Desktop launcher is pre-written (same content and ownership
#     marker Zeus itself maintains).
#   * A systemd USER service supervises the app in the graphical session
#     (Restart=on-failure) — updates hand off cleanly, crashes relaunch.
#   * Downloads are verified against the release manifest's sha256 — the
#     factory install rides the same chain of custody as every field update.
#   * Idempotent: run it again and it converges, never duplicates.
#
# Run as the OPERATOR account (the one the kiosk logs in as), not root.

set -eu

MANIFEST_URL="${ZEUS_MANIFEST_URL:-https://github.com/abhishekprakash22/zeus/releases/latest/download/latest.json}"
APP_DIR="$HOME/Applications"
APP_PATH="$APP_DIR/OpenhpsdrZeus.AppImage"
CFG_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/openhpsdr-zeus"
UNIT_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
DESKTOP_DIR="$(xdg-user-dir DESKTOP 2>/dev/null || echo "$HOME/Desktop")"

say() { printf '  \033[1;32m*\033[0m %s\n' "$1"; }
die() { printf '  \033[1;31m!\033[0m %s\n' "$1" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] && die "run as the operator account, not root (sudo -u pi $0)"

mkdir -p "$APP_DIR" "$CFG_DIR" "$UNIT_DIR"

# ---- 1) acquire the image -------------------------------------------------
if [ "${1:-}" != "" ] && [ -f "${1:-}" ]; then
  say "using provided image: $1"
  cp "$1" "$APP_PATH.staging"
else
  command -v curl >/dev/null || die "curl is required"
  command -v python3 >/dev/null || die "python3 is required (present on Raspberry Pi OS)"
  say "fetching manifest: $MANIFEST_URL"
  MANIFEST="$(curl -fsSL "$MANIFEST_URL")" || die "manifest fetch failed"
  # Parse with a real JSON parser — the first release of this script parsed
  # with grep, guessed the schema wrong, and set -e killed it SILENTLY at the
  # failed assignment before its own error message could fire. Every
  # extraction below is set -e-safe and fails loudly.
  PARSED="$(printf '%s' "$MANIFEST" | python3 -c '
import json, sys
m = json.load(sys.stdin)
assets = m.get("assets") or (m.get("versions") or [{}])[0].get("assets") or []
for a in assets:
    name = (a.get("filename") or "").lower()
    if name.endswith(".appimage") and ("aarch64" in name or a.get("arch") in ("arm64", "aarch64")):
        print(a.get("url") or "")
        print(a.get("sha256") or "")
        print(m.get("latest") or m.get("version") or "")
        break
' 2>/dev/null)" || true
  [ -n "$PARSED" ] || die "could not parse manifest (schema mismatch?) — inspect: $MANIFEST_URL"
  URL=$(printf '%s\n' "$PARSED" | sed -n 1p)
  SHA=$(printf '%s\n' "$PARSED" | sed -n 2p)
  VER=$(printf '%s\n' "$PARSED" | sed -n 3p)
  [ -n "$URL" ] || die "manifest has no aarch64 AppImage asset — inspect: $MANIFEST_URL"
  [ -n "$VER" ] && say "latest release: $VER"
  say "downloading: $URL"
  curl -fL --progress-bar -o "$APP_PATH.staging" "$URL"
  if [ -n "${SHA:-}" ]; then
    GOT=$(sha256sum "$APP_PATH.staging" | cut -d' ' -f1)
    [ "$GOT" = "$SHA" ] || die "sha256 mismatch (want $SHA got $GOT)"
    say "sha256 verified"
  else
    say "manifest carried no sha256 — skipping digest check"
  fi
fi
chmod 755 "$APP_PATH.staging"
mv -f "$APP_PATH.staging" "$APP_PATH"
say "installed: $APP_PATH"

# ---- 2) seed the self-updater's path memory -------------------------------
printf '%s\n' "$APP_PATH" > "$CFG_DIR/appimage-path"
rm -f "$CFG_DIR/update-pending"          # golden images ship with no pending state
say "self-updater armed (path memory seeded, no pending sentinel)"

# ---- 3) managed Desktop launcher (same content Zeus maintains) ------------
if [ -d "$DESKTOP_DIR" ]; then
  cat > "$DESKTOP_DIR/openhpsdr-zeus.desktop" << EOF
[Desktop Entry]
Type=Application
Name=OpenHPSDR Zeus
Comment=Software-defined radio (self-updating AppImage)
Exec="$APP_PATH"
Icon=radio
Terminal=false
Categories=HamRadio;Network;AudioVideo;
X-Zeus-Managed=true
EOF
  chmod 755 "$DESKTOP_DIR/openhpsdr-zeus.desktop"
  command -v gio >/dev/null && gio set "$DESKTOP_DIR/openhpsdr-zeus.desktop" metadata::trusted true 2>/dev/null || true
  say "desktop launcher written"
fi

# ---- 4) systemd user service (the supervisor the appliance deserves) ------
cat > "$UNIT_DIR/zeus.service" << EOF
[Unit]
Description=OpenHPSDR Zeus (self-updating AppImage)
After=graphical-session.target
PartOf=graphical-session.target

[Service]
ExecStart=$APP_PATH
Restart=on-failure
RestartSec=3
# The self-update handoff replaces this process deliberately: a clean exit
# after 'INSTALL & RESTART' must NOT be resurrected as the OLD image while
# the supervisor shell is mid-swap — hence on-failure, not always.
Environment=APPIMAGE=$APP_PATH

[Install]
WantedBy=graphical-session.target
EOF
systemctl --user daemon-reload 2>/dev/null || true
systemctl --user enable zeus.service 2>/dev/null \
  && say "systemd user service enabled (zeus.service)" \
  || say "systemd user session not active here — unit installed; enable on first login: systemctl --user enable zeus.service"

# ---- 5) report ------------------------------------------------------------
say "provisioning complete"
printf '\n  Next (golden-image prep):\n'
printf '    1. Reboot; Zeus starts under zeus.service.\n'
printf '    2. FIRST RUN ONLY: wait for the WDSP wisdom bake to finish\n'
printf '       (one-time, can exceed an hour on a Pi 5) and verify RX.\n'
printf '    3. Set the shipped defaults (layout, G2 frame), then shut down\n'
printf '       cleanly and capture the image. Every unit inherits the baked\n'
printf '       wisdom and boots ready.\n'
