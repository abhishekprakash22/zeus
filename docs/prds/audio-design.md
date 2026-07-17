# Designer & UX Brief — Zeus Interactive TX Audio Chain "Map"

## 1. What we're building

An **interactive, click-to-configure visual signal-chain map** for the Zeus SDR client's TX path. Operators see the entire mic-to-antenna journey as a living diagram: each stage is a visible block, real-time meters flow through it, and clicking a block opens its parameters inline. Think *Thetis block diagram, but alive and editable.*

The reference aesthetic is the classic Thetis TX block diagram (orange "WDSP" panel feeding green "FPGA + RF" panel). We are **not** copying that palette — we are reinterpreting it in the Zeus visual language, which is faithful to the Hermes Lite 2 hardware front panel.

## 2. Hard design constraints — read these first

Read the actual stylesheet before drawing anything: `zeus-web/src/styles/tokens.css` and `zeus-web/src/styles/layout.css`. The aesthetic is a faithful interpretation of the **Hermes Lite 2 hardware front panel** — embossed dark chrome on a blue-gray workspace, with chunky beveled controls and a single restrained accent. Match that, do not invent.

**Non-negotiables:**

1. **Use the existing token variables, never hex literals.** If a value isn't in `tokens.css`, you don't get to use it. If you genuinely need a new token, propose it as an addition to `tokens.css` (with a justification) — don't sprinkle hex values across components.
2. **Primary accent is `var(--accent)` = `#4a9eff`** (LSB/USB blue, frequency text, hover/focus rings). This is the "Zeus blue" — used sparingly for state and focus, not for everything.
3. **State colors are reserved and meaningful:**
   - `var(--tx)` `#e63a2b` — TX state and gain-reduction only. Don't use red for "delete" or "warning."
   - `var(--power)` `#ffc93a` — output power digits only.
   - `var(--orange)` `#f28524` — currently only the QRZ Lookup button. Don't repurpose it.
4. **Panel chrome:** `var(--panel-top)` → `var(--panel-bot)` linear gradient with `var(--panel-border)` edge and `var(--panel-shadow)` (inset highlight + outer shadow). Every dock panel does this. The TX chain map is no exception.
5. **Workspace background:** `var(--bg-app)` blue-gray `#657486` showing *through gaps between panels*. Don't paint flat-black backgrounds — the blue-gray peeking through is part of the look.
6. **Type:** `var(--font-sans)` = **Archivo Narrow**. Tabular numerics for any meter values (`font-variant-numeric: tabular-nums`).
7. **Beveled, not flat.** Buttons and tabs use the `--btn-top`/`--btn-bot` gradient with `--btn-hl` inset highlight and `--btn-edge` shadow. The active state is the blue gradient (`--btn-active-top`/`--btn-active-bot`). The codebase has no flat/material buttons; do not introduce them.
8. **Waterfall / spectrum colors are a system:** `--spec-bg`, `--spec-line`, `--spec-fill` for the panadapter trace; the `--wf-0..--wf-6` ramp (black → blue → cyan → yellow → orange → red) for waterfalls and intensity scales. If you need a "heat" gradient on a meter or a stage, derive it from this ramp — don't pick new colors.
9. **Meter fill is the existing gradient:** `var(--meter-fill)` (green → amber → red, classic VU). Use it for any horizontal/vertical bar meter unless you have a specific reason not to.
10. **No default AI palette.** No purple-pink-cyan gradients. No "v0/shadcn glow." No neon. If a screen looks like it could be any startup's dashboard, it's wrong — Zeus should look like a piece of radio gear.
11. **No third-party node-graph library** (no react-flow, no rete.js, no react-diagrams). Hand-roll with SVG. The chain is strictly linear, has a fixed topology, and needs custom meter overlays — those libraries are 60-100 KB of features we won't use and they'll fight us on the meter render loop.
12. **Do not invent stages.** Only the stages listed in §4 are in scope. Don't add "AI Voice Enhance" or "Smart Limiter" boxes.
13. **Honor wired-vs-unwired truthfully.** Some stages are live in Zeus today; others exist in WDSP but aren't yet exposed. The UI must tell the operator which is which (see §5) — never imply a control works when it doesn't.

## 3. Visual & interaction goals

- The map fills a docked panel inside our existing `flexlayout-react` layout (so it must work at widths from ~720 px to ~1600 px and reflow cleanly).
- **At a glance**, the operator sees: signal flow direction, which stages are active, where headroom is being consumed, and where the signal is being clamped right now.
- **One click** on any block opens an inline parameter editor — sliders, toggles, EQ curves — without leaving the map. Modal overlay is acceptable; full-page navigation is not.
- Live meters flow continuously at ~30 Hz (data already pushed via SignalR `StreamingHub`). The map should feel alive even when no one is talking — quiet but responsive.
- **Audio flowing through the chain is the hero.** Stage chrome is supporting cast — it should recede when the signal is the focus and come forward only when the user hovers/interacts.

## 4. Stages in scope (TX chain, mic → antenna)

This is the *exact* list. Match Thetis terminology so HF operators recognise it instantly.

### Top panel — WDSP (software DSP, runs in `Zeus.Server` via P/Invoke)

| # | Stage | One-liner | Operator controls |
|---|---|---|---|
| 1 | **Mic Input / 20 dB Boost** | Selects mic vs line vs VAC; optional preamp | input source, boost on/off |
| 2 | **VAC Gain** | Per-channel virtual-audio-cable level | VAC1/VAC2 gain (dB) |
| 3 | **VOX / DEXP** | Voice-operated TX + downward expander | threshold, hang time, on/off |
| 4 | **TX Gain (Mic / Line)** | Pre-EQ panel gain | mic gain (dB), line gain (dB) |
| 5 | **Phase Rotator** | All-pass phase shift, asymmetry control | on/off, freq, stages |
| 6 | **TX Equaliser** | Pre-compression EQ (3-band or graphic) | per-band gain, on/off |
| 7 | **Leveller** | Slow soft limiter, evens speech dynamics | max gain (dB), on/off |
| 8 | **Continuous Frequency Compressor (CFC)** | Multiband peak compressor | per-band threshold/ratio, on/off |
| 9 | **Post-CFC Equaliser** | Spectral shaping after CFC | per-band gain, on/off |
| 10 | **Speech Processor (COMP)** | Hard wide-band compressor | comp level, on/off |
| 11 | **CESSB** | Controlled-envelope SSB peak control | on/off |
| 12 | **ALC** | Final hard limiter, always-on safety net | display-only (max gain fixed) |

### Bottom panel — Radio FPGA + RF (lives on the board, not in WDSP)

| # | Stage | One-liner | Operator controls |
|---|---|---|---|
| 13 | **Digital Up-Converter** | TXA → baseband I/Q at radio sample rate | display-only |
| 14 | **D-A Converter** | Radio's DAC | display-only |
| 15 | **Variable Attenuator (TX Level / Drive)** | Sets RF drive into PA | drive % (already wired in `DriveSetRequest`) |
| 16 | **Power Amplifier** | Onboard PA | per-band gain, max watts, disable (already wired in `PaSettingsPanel`) |
| 17 | **Low-Pass Filter Bank** | Band-selected harmonic filter | OC bit mapping per band (already wired) |
| 18 | **T/R + Antenna Switch** | Routes to Ant1/Ant2/Ant3 + RX | antenna select, OC bits |

The bottom panel exists primarily for situational awareness — the operator already configures it via `PaSettingsPanel.tsx`. **Click on any of stages 15-18 should deep-link into the existing PA Settings panel, not duplicate it.**

## 5. State affordances — make truth visible

Each block must communicate three things at a glance, without text labels:

1. **Wired status** — is this stage actually controllable in Zeus today?
   - **Live (full saturation, solid border in `var(--accent)`):** Mic Gain (#4), Leveller (#7), CFC (#8 — toggle only), TX EQ (#6 — toggle only), Phase Rotator (#5 — toggle only), Speech Processor (#10 — toggle only), ALC (#12, display-only), Drive (#15), PA (#16), LPF (#17), Antenna (#18)
   - **Coming soon (dashed border at reduced alpha, `var(--fg-3)` chrome):** Mic Boost (#1), VAC Gain (#2), VOX/DEXP (#3), TX Gain mic/line split (#4 partial), Phase Rotator parameters (#5 partial), TX EQ band gains (#6 partial), CFC band parameters (#8 partial), Post-CFC EQ (#9), CESSB (#11)
   - The dashed-outline blocks should still appear in the chain — operators need the mental model — but clicking them opens a "Not yet exposed in Zeus — track progress at [link]" tooltip, not a non-functional editor.

2. **Active/bypassed** — is the stage processing or passed through?
   - Active: solid `var(--accent)` border, signal-flow line in `var(--accent)`
   - Bypassed (operator turned it off): muted `var(--fg-3)` border, signal-flow line desaturated *but still visible* — show that signal is passing through, not stopped
   - Disabled by hardware/board state: cross-hatched fill

3. **Live behaviour** — what is the signal doing right now?
   - Each block embeds a small inline meter (peak + average bar, or gain-reduction bar where relevant). Data already exists in `tx-store.ts` from `TxStageMeters.tsx` (MIC_PK, EQ_PK/AV, LVLR_PK/AV/GR, CFC_PK/AV/GR, COMP_PK/AV, ALC_PK/AV/GR, OUT_PK/AV).
   - Use `var(--meter-fill)` for forward-signal bars.
   - Gain-reduction is shown in `var(--tx)` flowing *upward* (the signal is being held back).
   - Output power on the final stage in `var(--power)`.

## 6. Click-to-configure interactions

- **Single click on a Live block** — slides open an inline parameter panel (think drawer that pushes from the block, not a centered modal). Editor matches the existing `PaSettingsPanel.tsx` table style and the Tailwind 4 conventions in `zeus-web`.
- **Single click on a Coming-Soon block** — small tooltip popover, not a panel. No fake controls.
- **Single click on a Radio/FPGA block (15-18)** — opens the existing PA Settings flexlayout panel with the relevant section scrolled into view. Don't reimplement.
- **Hover** — block lifts subtly (focus ring in `var(--accent-soft)`, max 4 px), connection lines either side highlight, sibling blocks fade slightly. Keep this restrained — operators stare at this for hours.
- **Right-click / long-press** — quick toggle for on/off where applicable, plus "Reset to default."
- **Keyboard** — `Tab` walks the chain L→R; `Enter` opens the editor; `Esc` closes; `Space` toggles active state. Accessibility is not optional — HF operators are often older and benefit from keyboard-first.

## 7. Layout & responsiveness

- **Wide (≥1200 px):** Two horizontal lanes (WDSP top, FPGA bottom), signal flowing left → right, mirroring the Thetis block diagram. Stages 1–12 across the top, 13–18 across the bottom, vertical drop-line at the boundary representing the Ethernet handoff.
- **Medium (720–1200 px):** Single-column vertical stack, top-to-bottom flow. Each stage becomes a full-width row.
- **Narrow (<720 px):** Out of scope for v1. The map docks closed and shows a "open in wider panel" hint.

The map must *not* introduce horizontal scroll inside the dock panel. If it doesn't fit, reflow.

## 8. Technical integration (for the implementing engineer)

Things you do not need to invent — they exist:

- **Design tokens:** `zeus-web/src/styles/tokens.css` (read this first)
- **State store:** Zustand. Read live values from `state/tx-store.ts`; extend it for new toggles/sliders. Do not add Redux.
- **Live data feed:** SignalR `StreamingHub` already pushes TX stage meters at ~30 Hz via the framed protocol — no new transport needed.
- **Settings persistence:** existing `*-SetRequest` DTOs in `Zeus.Contracts/Dtos.cs`. New stages will need new DTOs; coordinate with backend before designing the editor surface for stages that aren't yet wired.
- **Component patterns to mimic:** `PaSettingsPanel.tsx` (parameter tables), `TxStageMeters.tsx` (meter coloring + state thresholds), `TxFilterPanel.tsx` (slider feel)
- **Layout host:** `flexlayout-react` — your component is a single dockable panel, not a route.
- **CSS:** Tailwind 4 + the tokens file. Inline `className` strings are the prevailing style — match it. When you need a chrome-heavy element (panel, beveled button, VFO-style tab), reach for the named classes in `layout.css` rather than re-deriving the gradient stack.

## 9. Reference reading, in priority order

1. **`zeus-web/src/styles/tokens.css`** — the source of truth for palette, typography, spacing, and chrome. **Read this before opening Figma.**
2. **`zeus-web/src/styles/layout.css`** — concrete examples of how panel chrome, beveled buttons, VFO tabs, and the topbar are constructed from those tokens. Patterns like `.vfo-tab`, `.topbar`, `.control-strip`, `.btn.pulsing` are the visual library you compose from.
3. **Screenshots in the repo root** (`design-v3.png`, `desktop-1440.png`, `master-*.png`, `tablet-768.png`) — what the live app actually looks like. Use these, not the Thetis screenshot, as the visual baseline.
4. **`zeus-web/src/components/PaSettingsPanel.tsx`** — closest functional cousin (parameter table inside a chrome panel).
5. **`zeus-web/src/components/TxStageMeters.tsx`** — meter coloring, overdrive thresholds, live-data wiring; the new chain map's per-block meters should feel like these.
6. **`zeus-web/src/components/TxFilterPanel.tsx`** — slider feel and bandwidth control patterns.
7. **`docs/rca/2026-04-21-tx-stage-meters-alc-gr-sign-mic-pk.md`** — gain-reduction sign convention. Direction matters; getting it backwards on the chain map will mislead operators.
8. The classic Thetis block diagram (the screenshot) — for **stage names, ordering, and the operator's mental model only**. Its color/typography is *not* a visual reference. The Zeus chain map should look like Zeus, not like Thetis.
9. Sibling repos `../Thetis` and `../OpenHPSDR-Thetis` — engineering reference for what each stage does. Do not copy their WinForms aesthetic.

## 10. Deliverables

1. **High-fidelity Figma frames** at 1440 × 900 (wide) and 800 × 1100 (medium), showing:
   - Idle state (no TX active)
   - Mid-speech state (mic peaking, leveller pulling 3 dB, CFC active, ALC just kissing limit)
   - Edit-open state (operator clicked the TX EQ block, drawer is open showing band gains)
   - Disabled-block state (one stage bypassed)
2. **Interaction spec** — short markdown describing each interaction (hover, click, right-click, keyboard) with the existing component it borrows from. Bullet points, not prose.
3. **Production React component** in `zeus-web/src/components/TxChainMap.tsx`, plus any new Zustand store slices and stub DTOs needed. Wire to `state/tx-store.ts` for live data; for "coming soon" stages, render the affordance but route clicks to a tooltip — no fake controls.
4. **A dock-panel registration** so the map appears in the flexlayout panel chooser alongside `TxStageMeters` and `PaSettingsPanel`.

## 11. What we are explicitly *not* asking for

- A new audio engine. WDSP is the engine. This is a UI over existing capabilities.
- A new color palette, new typography, or "modernised" Zeus look. Match what's there.
- Drag-and-drop signal routing. The chain topology is fixed.
- Mobile design. The flexlayout panel handles mobile separately.
- A dependency on react-flow, rete.js, react-diagrams, or any node-graph library. Hand-roll SVG.
- A "minimap" or "zoom to fit" — the chain fits the panel by design.

## 12. Definition of done

- Renders correctly at 720 px, 1024 px, 1440 px panel widths
- Updates at ≥30 Hz without dropped frames during active TX (use the existing SignalR stream, don't poll)
- Every Live block's editor round-trips through the existing `*-SetRequest` flow and persists across reconnects
- Every Coming-Soon block is honest about its status — no placebo controls
- Every color, font, and chrome treatment derives from `tokens.css` / `layout.css`. No stray hex values in the diff.
- Passes the maintainer's eyeball test for visual coherence with the Hermes-Lite-2-faithful Zeus aesthetic. If anything looks like a "default AI gradient" or a generic dashboard, it's wrong.
