#!/bin/bash
# Zeus kiosk launcher — Raspberry Pi appliance presentation.
# Waits for the Zeus server, then opens Chromium full-screen with no browser
# chrome, no crash bubbles, and touch-friendly defaults. The window is created
# at its final size on frame one, which also sidesteps the V3D first-composite
# garble seen with plain --app windows.
ZEUS_URL="${ZEUS_URL:-http://localhost:6060}"

# Wait (up to ~60 s) for the server to answer before launching the browser —
# a kiosk that races the service shows a white error page forever.
for i in $(seq 1 60); do
  curl -sf -o /dev/null "$ZEUS_URL" && break
  sleep 1
done

# Hide the mouse cursor when idle if unclutter is installed (optional):
#   sudo apt install unclutter
command -v unclutter >/dev/null && unclutter -idle 2 -root &

BROWSER=$(command -v chromium-browser || command -v chromium)
exec "$BROWSER" \
  --kiosk "$ZEUS_URL" \
  --start-fullscreen \
  --noerrdialogs \
  --disable-session-crashed-bubble \
  --disable-infobars \
  --overscroll-history-navigation=0 \
  --disable-pinch \
  --autoplay-policy=no-user-gesture-required \
  --check-for-update-interval=31536000
