# Zeus Pi kiosk mode

Full-screen appliance presentation: no tabs, no address bar, cursor hides
when idle (if `unclutter` is installed), browser waits for the Zeus server
before opening. Creating the window at its final size on frame one also
avoids the V3D first-composite garble seen with plain `--app` windows.

Install on the Pi (Zeus already deployed and starting at boot):

    cd installers/pi-kiosk && ./install.sh

Manual run: `~/zeus-kiosk.sh`.  Exit: **Alt+F4**.
Remote server: `ZEUS_URL=http://<host>:6060 ~/zeus-kiosk.sh`.
