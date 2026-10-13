## Troubleshooting

### Check my settings

Before working through the table below, let the radio check itself: **Settings → TROUBLESHOOT** (or **Check my settings** in the ASK drawer). Pick what's wrong and press **CHECK**. The radio looks at the settings and conditions that most often cause that problem and lists each with its actual value and a verdict — **LIKELY CAUSE**, **CHECK** or **OK** — plus the exact fix. It runs entirely on the radio: no AI, nothing is sent anywhere. The same check is included in every **Report a problem**, and the assistant uses it too.

What it looks at, by problem:

- **No or quiet receive audio:** each receiver's mute, AF gain and AGC-T (and whether Auto AGC-T is on), AGC mode, squelch, the attenuator, filter width, the audio output being muted or playing on a different device, the radio connection and p2app.
- **Weak signals / high noise:** AGC-T, attenuator, filter width, ADC overload.
- **Receive audio distorted:** ADC overload, AGC mode, noise reduction strength.
- **Low or no transmit power:** drive, the drive limit, TUN power, mic gain (voice modes), TX audio source, TX filter width, p2app.
- **Transmit audio problems:** mic gain too low or too high, TX audio source, TX filter, leveler gain, PureSignal state.
- **PureSignal won't calibrate:** PS armed, calibration stalled, feedback level (too hot or too weak) and auto-attenuate, HW peak changed from default.
- **Radio won't connect:** connection state, p2app not installed / stopped / restarting, another program holding a network port.
- **Can't connect remotely:** remote password and callsign set, the callsign claimed by another radio, the radio losing its connection to the remote service, clock sync.
- **Remote audio choppy or laggy:** the network path (wired vs Wi-Fi, direct vs relayed), host temperature and power.
- **FT8 / digital modes:** clock sync, mode, receive filter width, drive, TX audio source.
- **Every problem:** the radio computer's power supply (under-voltage), CPU temperature and throttling, and free storage.

The thresholds behind the verdicts are conservative defaults; a **CHECK** means "worth a look", not "wrong".

| Symptom | Likely cause | Fix |
|---|---|---|
| No Saturn row on Discover | p2app not running yet | Wait one 10 s cycle; check Settings → UPDATES → P2APP. `NoBinary` → run UPDATE P2APP once |
| Row shows 127.0.0.1 instead of LAN address | No Ethernet cable | Normal — same radio, connect anyway |
| P2APP status "found already running" (Adopted) | A p2app you (or a service) started | Fine for normal use; kill it yourself + press START if you want it supervised, or for native sessions |
| STOP button refuses with "running outside ANAN Core's management" | Adopted p2app — never killed by design | Kill the pid yourself, then START |
| "UDP 1024 is owned by another process" starting a native session | Externally-managed p2app | Stop it, retry |
| UI looks stale after an update | Browser cached the old interface | Reload once (Ctrl+Shift+R) |
| p2app update failed | Upstream build error / missing tools | Read the log in the panel; previous binary restored automatically. Radio needs gcc, make, libi2c-dev, libgpiod |
| Remote stuck at "negotiating a media path" | Network path, not the password | Wait for the 45 s verdict; RELAY should engage behind CGNAT |
| "Another operator is connected" | Single-operator slot in use | Wait ≤30 s after the other session vanishes |
| Callsign card says "already taken" | Claimed by another (or a previous rebuild of this) radio | Save a variant (CALL/2); a lost claim lapses ~60 days offline |
| Remote mic not heard on the air | TX audio source armed to a radio jack | RADIO tab → TX audio source → **Host** (the hint chip tells you) |
| No mic on a LAN phone browser | HTTP origin — browsers require HTTPS for mic | Use the HTTPS address from the SERVER tab |
| PS won't calibrate, over-drive banner | Chain in severe compression | Lower drive or raise Feedback atten until fits complete |
| IMD3/5 reads "—" | Products at/below display floor, or very wide TX span | Expected post-convergence; narrow the span to measure |
| Slider/AGC "stuck in auto" | Auto-AGC-T armed on that receiver | Drag that receiver's AGC-T slider — the drag is the disarm |
| 3D pill shows 3D✕ | Browser can't complete WebGPU init (includes the Pi's screen) | 2D keeps running; use 3D on a desktop with WebGPU |
