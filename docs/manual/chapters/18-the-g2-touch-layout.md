## The G2 touch layout (the 8-inch front glass)

Settings → DISPLAY → **G2 touch layout** swaps the desktop transport bar for a touch-first layout while connected. It is also how the radio's own screen runs out of the box. Turning the option off restores the desktop workspace and standard theme instantly.

### The panes

The workspace becomes stacked receiver panes — RX1 over RX2, each with its own spectrum, waterfall, band, dB scales (levels fully independent, on spectrum and waterfall), ZOOM, and waterfall SPEED. Receivers split the glass 50/50 when RX2 is enabled; with RX2 off, RX1 takes the whole display. **Drag the bar between the panes** to resize the split, and the bar *inside* each pane to set its spectrum/waterfall ratio — both remembered **on the radio**, so every client and restart shares them. Pinch on either pane zooms that receiver alone.

**Tap anywhere on the inactive pane to make that receiver active** — the first tap only selects, it never tunes. The amber flag marks the active receiver; the drawer's BAND/MODE/FILTER, the top AGC slider, and the analog S-meter all follow it. **Audio follows the active receiver** — the inactive pane is muted until you tap it (both unmute when you leave the layout), including while Settings is open.

### The flags

Each receiver's VFO flag shows RX number, the real VFO readout (scroll a digit to step that decade; click the digits to type kHz inline), mode, filter width, and a live mini S-meter with S-point markings, S readout, and peak-hold tick (RX1 from the calibrated meter, RX2 estimated from its spectrum). SPLIT lights a red SPLIT▸B tag on RX1's flag. A DSP status row lights NR / NB / ANF / SNB when engaged (the NB·NR sheet is the editor). Two chips ride the flag:

- **CTRL** opens that receiver's control popover: **MODE** (full mode list in a legible dropdown), **AF**, **AGC-T** (drag it to take manual control — Auto disengages and the "· auto" tag clears), **MIC** (−40…+10 dB), **DRV** (0…100 %), and **MUTE**. The popover dismisses from any dead space or Escape; MIC and DRV are the shared TX controls, shown on the active pane.
- **STEP** cycles the tuning step.

### The drawer

The slim top strip holds the page tabs — **BAND · MODE · FILTER · NB·NR · RADIO · DISPLAY · TX** — each opening its bottom sheet *for the focused receiver* (the active tab wears an accent underline). BAND/MODE/FILTER sheets close themselves after a pick (**PIN** keeps them open); NB·NR, RADIO (antennas), DISPLAY, and TX are panels that stay.

The full-height row below is the **key row**: **MOX** fixed on the left, four operator-chosen keys, and **NEXT**, which cycles **three sets of four**. The **✎** key opens a picker on each slot. One key lives in exactly one slot across all three sets — picking a key that already lives elsewhere **swaps** the two (the picker marks such keys with `⇄ set-number`), so duplicates can't exist and no slot is ever blank. The stock arrangement covers every assignable key:

| Set | Keys |
|---|---|
| 1 | **TUN · MON · PS · CTUN** |
| 2 | **SPLIT · RIT · DIV · MUTE** (mute follows the focused receiver) |
| 3 | **LOCK** (VFO lock) · **2TON** (two-tone generator — keys the transmitter, lights TX red) · **CW⌁** (CW decoder) · **FULL SCR** |

**PRE** joins the pool only on the one board where the preamp bit does anything. All keys carry the same safeguards as their desktop originals, at finger size. Key-set assignments are stored on the radio. A compact FWD / SWR / ALC readout (label, bar, value) sits inline in the transport row.

### The left rail

**DISC** (tap → red SURE? → tap within 3 s to disconnect; warns on unsaved TX-audio edits), **FULL SCR** (browser full screen), **SCR / CONTROLS** (the full desktop control cluster — STEP, FRONT-END, AGC, SQL, AF, ROGER beep, VIEW, plus a row with SPLIT, RIT, DIV, the CW decoder toggle, and **NIGHT** shack dimming — as a touch panel), and **AUDIO PROC** (the TX audio processing panel — CFC, EQ, leveler — with live TX stage meters).

### Cards, themes, and extras

The analog S-meter and the per-receiver bandwidth filter cards float over the panes — drag by the title strip, resize by the corner handle, close with ✕ (restore pills appear top-right). Card positions and hidden-card choices persist per device. DISPLAY offers **five G2 themes** (Core Blue, Amber, Night Red, Phosphor, Ice) recoloring the chrome — TX red never changes, spectrum/waterfall keep their own colors, and NIGHT dimming composes with any theme. A fixed **multi-encoder badge** top-right names the front panel's multifunction encoder assignment and flashes when it changes (§18). The whole layout wears the graphite-and-amber G2 look; the desktop header row is gone and the panes take its space.
