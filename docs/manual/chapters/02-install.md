## Installing & Updating Zeus

Zeus is delivered as a ready-to-run installer for every platform it supports, so you do not need to be a programmer to get on the air. Pick the download for your operating system, install it the same way you would any other app, and launch it. This chapter walks through each platform, the one extra step macOS requires, what happens the very first time you start Zeus, how to build it yourself if you prefer, and how the built-in update feature keeps you current.

### Where to download

All official builds live on the project's **Releases page** on GitHub. From there, grab the file that matches your machine:

| Platform | What to download |
|---|---|
| Windows (Intel/AMD, 64-bit) | `Zeus-<version>-win-x64-setup.exe` (background service) or `Zeus-Desktop-<version>-win-x64-setup.exe` (desktop window) |
| Windows on ARM (Surface Pro X, Snapdragon laptops) | `Zeus-<version>-win-arm64-setup.exe` or `Zeus-Desktop-<version>-win-arm64-setup.exe` |
| macOS (Apple Silicon) | `openhpsdr-zeus-<version>-macos-arm64.pkg` |
| Linux / Raspberry Pi (x64 and arm64) | `.tar.gz` tarball, or the `.AppImage` for a desktop window |

Each build is **self-contained** — it carries its own copy of the .NET runtime and the WDSP DSP engine, so there is no separate runtime to install first.

A quick word on the two flavors you will see on Windows, macOS, and Linux. Since the project consolidated everything into one program, every platform ships **two icons**:

- **Zeus** — the full desktop app. It opens its own window with the complete console inside, no web browser needed.
- **Zeus Server** — a small status window that runs the radio backend and lists the LAN addresses you can reach it at, with a Stop button. Use this when you want to drive Zeus from a browser on another computer, tablet, or phone on your network.

There is also a third, invisible mode: launched with no special flag, Zeus runs **headless** as a pure background service (no window at all). That is what the Raspberry Pi and other always-on server setups use.

### macOS

Run the `.pkg` installer and click **Install**. It installs both `OpenHPSDR Zeus.app` and `OpenHPSDR Zeus Server.app` into Applications. Tagged releases are signed and notarized, so Gatekeeper should accept the installer without an `xattr` workaround.

### Windows

Run the `.exe` installer and follow the prompts. The installer also installs Microsoft's Visual C++ Runtime if your machine doesn't already have it — Zeus's audio and DSP libraries need it, and a fresh Windows install often lacks it. If that runtime is missing, Zeus silently falls back to a placeholder mode with no panadapter, no audio, and no transmit power, so letting the installer handle it matters. The installer skips this step automatically when a compatible runtime is already present.

Choose the **x64** installer for a normal Intel or AMD PC, or the **arm64** installer for a Surface Pro X or Snapdragon-class ARM laptop. A note for ARM operators: the radio, panadapter, and WebSocket data path are all verified on Windows on ARM, but microphone capture and the audio device list have not yet been smoke-tested on real ARM hardware. If you hit audio quirks on an ARM laptop, that is the most likely area.

### Linux and Raspberry Pi

On a desktop Linux machine, the simplest path is the **AppImage** — make it executable and run it for a full desktop window. The **tarball** is the headless option: unpack it and run the program directly to serve the UI to browsers on your LAN.

Zeus runs well on a **Raspberry Pi 4 or 5** (and other arm64 boards). A few Pi-specific notes:

- You **must** use a **64-bit OS** (Raspberry Pi OS 64-bit or Debian arm64). The arm64 build will not run on 32-bit Raspberry Pi OS.
- For the AppImage desktop window you need the WebKitGTK library installed (`sudo apt-get install -y libwebkit2gtk-4.1-0`).
- **Discovery with both Wi-Fi and Ethernet on:** the radio-finding broadcast goes out only one network interface — usually Wi-Fi — so if your radio is on the wired segment it may never appear in the discovery list. The fix is either to turn Wi-Fi off while using Ethernet, or simply to connect to the radio by typing its IP address directly (a manual connection always routes correctly regardless of interface).
- **Audio device:** the Pi defaults to its first sound card, which is often HDMI. If your audio comes out the wrong place, point ALSA at your USB audio device.

A Pi 4 handles RX comfortably (roughly 15-25% CPU at 48 kHz). Heavy modes such as PureSignal at 192 kHz are likely marginal on a Pi 4.

### First launch

The very first time you start Zeus on any platform, it builds a one-time DSP "wisdom" cache that tunes the math engine to your hardware. **This can take a few minutes** — that is normal, and every launch afterward is instant. Don't be alarmed if the first start feels slow.

Zeus also asks one question on first launch: whether to **automatically send crash logs to the developers**. If Zeus ever crashes, a crash report (logs and a diagnostics snapshot) helps the problem get fixed without you having to re-create it. Answer **Yes** and Zeus turns on the two Remote Diagnostics switches for you; answer **No thanks** and nothing is shared — you can change your mind any time under **Settings → Server → Remote Diagnostics**. You are asked once, and never again.

Once Zeus is up, the desktop app shows the console directly. If you are running the **Zeus Server** mode and connecting from a browser, point the browser at the LAN address the server window lists (typically `http://<your-computer-ip>:6060`).

If you are using a separate device — a native mobile or desktop wrapper, or a browser that needs to reach a server elsewhere on your network — open **Settings → Server URL** and enter the address of the machine running the Zeus backend, for example `http://192.168.1.23:6060`. Plain browser users on the normal setup leave this field **blank**; the page already talks to the server that served it. Saving a new address reloads the page so everything reconnects cleanly. That same panel also lists any secure (HTTPS) LAN addresses your server is offering, which is what a phone browser needs in order to use its microphone for transmit.

### Building from source (optional)

If you would rather build Zeus yourself, clone the repository **with submodules** — the speech-decoder model and the audio-plugin SDK live in submodules, and the web build fails without them:

```
git clone --recurse-submodules https://github.com/Kb2uka/openhpsdr-zeus.git
```

Already cloned without them? Run `git submodule update --init --recursive`. Full developer prerequisites and the build loop are documented in the project wiki's Developer Guide.

### Staying up to date

Zeus checks for new releases on its own. Packaged installs query the official release feed when they start, and when a newer version is out you'll see an update notice. To check or update on demand, open **Settings → Updates** (the "Software Updates" panel). It shows:

- **Installed** — the version you're running now.
- **Latest** — the newest production release available.
- **Platform** — the operating system and processor the panel detected, so it can offer the right download for your machine.
- A status line that reads *Up to date*, *Version X available* (highlighted), or *Checking releases*, along with when it last checked.

Two buttons sit at the bottom. **Check for Updates** re-queries the release feed right away. **Update Now** opens the correct installer, package, AppImage, or tarball for your platform — it picks the matching download automatically. Zeus does not silently overwrite itself: you run the downloaded installer and restart Zeus, exactly as you did the first time.

If you run Zeus from a **source checkout** instead of an installer, the Updates panel tells you which branch and revision you're on and how it compares to upstream. To pull and apply updates, use the bundled update scripts — `scripts/update.ps1` on Windows, or `scripts/update.sh` on macOS and Linux. Each one fast-forwards your checkout to the latest code (refusing if you have uncommitted changes so your work is never clobbered), rebuilds the web UI and the backend, and then asks you to restart Zeus. A pull only changes the source — you must rebuild and restart for it to take effect.

> **Tip:** after any update, give Zeus an extra moment on first launch — if the DSP engine changed, it may rebuild the wisdom cache once more before settling back to instant starts.
