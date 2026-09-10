## Quick start — the appliance flow

On a G2 / G2 Ultra with ANAN Core factory-installed:

1. **Power on.** The radio boots into ANAN Core full screen on the front glass.
2. **Wait ~10 seconds.** The Discover tab fills in by itself:
   - a **Saturn** row with a **Connect** button — this is your radio's data plane (p2app), started and watched by ANAN Core automatically. With an Ethernet cable in, the row shows the radio's LAN address; with no cable at all it shows `127.0.0.1`. Both are the same radio; both work.
   - a **PCIe · DETECTED** badge row — proof the software can see the radio on its own internal bus. Informational; you don't connect through it (see §24).
3. **Tap Connect.** You're on the air. No terminal, no setup, no cables required.

The Discover header names the board honestly from its Protocol-2 discovery ID — a G2/Saturn shows **Saturn (ANAN G2)**, a G2E shows **Hermes C10 (ANAN G2E)** — so you always know which radio a row belongs to.

To finish a session: **Exit** (closes ANAN Core and everything it manages) or the **⏻ SHUT DOWN** button (powers the radio down — tap it, then confirm **SURE?** within ten seconds; refused while transmitting).
