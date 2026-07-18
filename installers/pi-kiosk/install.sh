#!/bin/bash
# Install the Zeus kiosk autostart for the current user (run on the Pi).
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
cp "$HERE/zeus-kiosk.sh" "$HOME/zeus-kiosk.sh"
chmod +x "$HOME/zeus-kiosk.sh"
mkdir -p "$HOME/.config/autostart"
sed "s|/home/pi/zeus-kiosk.sh|$HOME/zeus-kiosk.sh|" "$HERE/zeus-kiosk.desktop" \
  > "$HOME/.config/autostart/zeus-kiosk.desktop"
echo "Installed. Zeus opens full-screen at next login."
echo "Exit kiosk: Alt+F4. Disable: rm ~/.config/autostart/zeus-kiosk.desktop"
