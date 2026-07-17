# Installation Guide

Zeus is distributed as native installers for Windows, macOS, and Linux.

## Windows

### Requirements
- Windows 10 or later (64-bit)
- 4 GB RAM minimum, 8 GB recommended

### Installation Steps

1. Download `openhpsdr-zeus-X.Y.Z-win-x64-setup.exe` from the [latest release](https://github.com/Kb2uka/openhpsdr-zeus/releases/latest)
2. Run the installer
3. Follow the installation wizard prompts
4. Zeus will be installed to `C:\Program Files\OpenHPSDR-Zeus` by default
5. The Start Menu gets two entries:
   - `OpenHPSDR-Zeus` opens the native desktop window
   - `OpenHPSDR-Zeus Server` starts the LAN server with a small status window
6. The installer creates the desktop-mode desktop shortcut by default. The server-mode desktop shortcut is optional on the installer task page.
7. Launch `OpenHPSDR-Zeus` for normal single-machine use.

### Notes
- The installer includes the .NET runtime, the Visual C++ runtime needed by the native DSP/audio libraries, and the bundled native libraries
- Windows Defender SmartScreen may show a warning for unsigned applications - click "More info" then "Run anyway"
- To uninstall, use Windows Settings > Apps > OpenHPSDR-Zeus

---

## macOS

### Requirements
- macOS 11 (Big Sur) or later
- Apple Silicon (M1/M2/M3). Intel users can build from source.
- 4 GB RAM minimum, 8 GB recommended

### Installation Steps

1. Download the macOS package for your Mac:
   - **Apple Silicon (M1/M2/M3)**: `openhpsdr-zeus-X.Y.Z-macos-arm64.pkg`

2. Open the downloaded package

3. Click through the installer

4. Launch `OpenHPSDR Zeus` from your Applications folder or Launchpad

5. Use `OpenHPSDR Zeus Server` when you want the LAN status window and browser access

### Troubleshooting

If macOS reports that the package or app cannot be verified, confirm that you downloaded the signed `.pkg` from the latest Zeus release.

### Uninstallation
- Drag `OpenHPSDR Zeus.app` and `OpenHPSDR Zeus Server.app` from Applications to Trash

---

## Linux

### Requirements
- Linux x64 distribution (Ubuntu 20.04+, Debian 11+, Fedora 35+, or equivalent)
- 4 GB RAM minimum, 8 GB recommended
- Desktop environment (for automatic browser launching)

### Installation Steps

1. Download `zeus-X.Y.Z-linux-x64.tar.gz` from the [latest release](https://github.com/Kb2uka/openhpsdr-zeus/releases/latest)

2. Extract the archive:
   ```bash
   tar -xzf zeus-X.Y.Z-linux-x64.tar.gz
   ```

3. (Optional) Move to a permanent location:
   ```bash
   sudo mv zeus-X.Y.Z-linux-x64 /opt/zeus
   ```

4. Run Zeus:
   ```bash
   cd /opt/zeus  # or wherever you extracted it
   ./zeus
   ```

5. Your default browser will open to `http://localhost:6060`

The release archive bundles the FFTW runtime libraries used by WDSP. System
FFTW packages are only needed when rebuilding native WDSP locally from source.


### Creating a Desktop Launcher (Optional)

Create `~/.local/share/applications/zeus.desktop`:
```ini
[Desktop Entry]
Type=Application
Name=Zeus
Comment=OpenHPSDR SDR Client
Exec=/opt/zeus/zeus
Icon=/opt/zeus/icon.png
Terminal=false
Categories=Network;HamRadio;
```

Then run:
```bash
update-desktop-database ~/.local/share/applications
```

### Uninstallation
- Simply delete the extracted directory

---

## First Run

On the **first run only**, Zeus will initialize WDSP/FFTW wisdom files. This takes 1-3 minutes on a modern CPU. You'll see:

```
Optimizing FFT sizes through 262145
Please do not close this window until wisdom plans are completed.
```

**Do not** click "Discover" or "Connect" in the web UI until you see:

```
wdsp.wisdom ready result=1 (built)
```

Subsequent starts will be instant as the wisdom is cached.

---

## Updating Zeus

When a new version is available, Zeus will show a notification in the Settings > About panel.

### Windows
- Download and run the new installer
- It will automatically upgrade your existing installation

### macOS
1. Download the new `.pkg`
2. Run the installer
3. Restart Zeus

### Linux
1. Download the new tarball
2. Extract to replace your existing installation
3. Your settings are preserved (stored in `~/.local/share/Zeus/`)

---

## Configuration and Data Locations

### Windows
- Settings: `%LOCALAPPDATA%\Zeus\zeus-prefs.db`
- WDSP Wisdom: `%LOCALAPPDATA%\Zeus\wdspWisdom00`
- Logs: `%LOCALAPPDATA%\Zeus\logs\`
- Startup/crash diagnostics: `%LOCALAPPDATA%\Zeus\zeus-startup.log`
- Native crash dumps and unclean-launch snapshots: `%LOCALAPPDATA%\Zeus\crash-dumps\`

### macOS
- Settings: `~/Library/Application Support/Zeus/zeus-prefs.db`
- WDSP Wisdom: `~/Library/Application Support/Zeus/wdspWisdom00`
- Logs: `~/Library/Application Support/Zeus/logs/`

### Linux
- Settings: `~/.local/share/Zeus/zeus-prefs.db`
- WDSP Wisdom: `~/.local/share/Zeus/wdspWisdom00`
- Logs: `~/.local/share/Zeus/logs/`

---

## Support

- **Issues**: https://github.com/Kb2uka/openhpsdr-zeus/issues
- **Documentation**: https://github.com/Kb2uka/openhpsdr-zeus
- **License**: GNU GPL v2 or later

---

## Building from Source

If you prefer to build Zeus from source instead of using the pre-built installers, see the [README.md](../README.md) for developer setup instructions.
