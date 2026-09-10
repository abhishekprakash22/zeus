## Installing ANAN Core

### On the radio (G2 / G2 Ultra appliance)

New radios ship with ANAN Core installed. To install on a fresh Raspberry Pi OS (Debian Trixie, 64-bit):

1. Install the prerequisites: `sudo apt install libwebkit2gtk-4.1-0 libfuse2t64` (and `unclutter` for a kiosk screen).
2. Run the factory install script from the repository (`factory-install.sh`). It installs the AppImage, the kiosk launcher, the systemd **user** unit (`zeus.service`), and a Desktop shortcut named **ANAN Core**.
3. Reboot. The radio boots straight into the full-screen appliance.

The service is a *user* unit — manage it with `systemctl --user status zeus` as the `pi` user, not with `sudo systemctl`. The authoritative application log is `~/.local/share/Zeus/logs/zeus-app.log` (rotated at 2 MB into `.1`/`.2`); journald deliberately carries nothing.

### On a desktop

Windows, macOS, and Linux builds are published on the project's GitHub Releases page. Install/unpack, run, and point the Discover tab at your radio's LAN. Desktop and radio run the *same* application — everything in this manual applies to both, except the appliance-only sections (kiosk, FPGA flashing, shutdown, p2app supervision), which appear only when ANAN Core is running on the radio itself.

### Staying up to date

On the radio, updates are one button: Settings → **UPDATES** → INSTALL & RESTART (§22). On desktops, the same tab shows installed vs latest and links the right installer for your platform.
