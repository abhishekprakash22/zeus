## p2app — the data plane, managed for you

p2app (by Laurence Barker G8NJJ) is the small program that turns the Saturn board into a network radio. **You never start or stop it yourself.** ANAN Core:

- **starts** it when the radio boots (finding it whether it lives in your own Saturn checkout or a managed one),
- **restarts** it within seconds if it ever crashes,
- **stops** it when you press Exit or shut down,
- **adopts** (leaves alone, never kills) a p2app you started yourself or run as a system service,
- **pauses** it automatically when an expert native-PCIe session needs the hardware, and brings it back after,
- **restarts** it once, by itself, if the G2's front panel hasn't come alive a few seconds into a session.

Status any time: Settings → **UPDATES** → P2APP section. The status line speaks operator language — *"Running — started and kept alive by ANAN Core"* (Supervised), *"found already running"* (Adopted), Paused, Backoff, NoBinary — with the live pid.

**Developer STOP/START.** The same section carries a STOP/START toggle for developers who need the UDP port free. STOP halts only a p2app that ANAN Core itself started; an *adopted* p2app is never killed — the refusal ("p2app is running outside ANAN Core's management") appears in amber right under the button. A developer STOP holds: nothing auto-resurrects the process until you press START. To clear an orphaned/adopted p2app, kill it yourself and press START to get a supervised child back.

**Updating p2app.** Settings → UPDATES → **UPDATE P2APP** pulls the latest source from the Saturn repository and rebuilds on the radio. If there is nothing new, it says so — *"already up to date"* — and leaves the running process untouched. During a real build the Saturn row disappears for a couple of minutes and returns on the new version by itself. **If the build fails, nothing is lost:** the previous binary is restored automatically (and kept on disk as `p2app.zeus-previous`).
