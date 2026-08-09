// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.

import { CwKeyer } from '../../components/design/CwKeyer';
import { abortCw, sendCw } from '../../api/cw';
import { useCwStore } from '../../state/cw-store';

// Hard cap mirrors Zeus.Server.Hosting/CwSettingsStore.cs MaxMacros. If
// the server bumps the cap, also bump this so the UI's "Add" button
// matches. (We could fetch it dynamically, but a constant is cheaper
// and only changes during epic-scale revisits.)
const MAX_MACROS = 32;

export function CwPanel() {
  const settings = useCwStore((s) => s.settings);
  const status = useCwStore((s) => s.status);
  const setSettingsLocal = useCwStore((s) => s.setSettingsLocal);
  const commitDebounced = useCwStore((s) => s.commitDebounced);
  const patchSettings = useCwStore((s) => s.patchSettings);
  const setMacro = useCwStore((s) => s.setMacro);
  const addMacro = useCwStore((s) => s.addMacro);
  const removeMacro = useCwStore((s) => s.removeMacro);

  return (
    <div style={{ flex: 1, overflow: 'auto' }}>
      <CwKeyer
        wpm={settings.wpm}
        // Split-write pattern fixes the "slider snaps back" race. The
        // local setter updates the store immediately so the slider
        // tracks the pointer; the debounced commit schedules a single
        // PUT after the operator stops dragging.
        setWpmLocal={(v) => setSettingsLocal({ wpm: v })}
        setWpmCommit={(v) => commitDebounced({ wpm: v })}
        keyerMode={settings.keyerMode}
        // One-shot discrete choice (not a drag) — optimistic save with
        // rollback, same as a macro edit.
        setKeyerMode={(m) => void patchSettings({ keyerMode: m })}
        sidetoneHz={settings.sidetoneHz}
        setSidetoneHzLocal={(v) => setSettingsLocal({ sidetoneHz: v })}
        setSidetoneHzCommit={(v) => commitDebounced({ sidetoneHz: v })}
        sidetoneGainDb={settings.sidetoneGainDb}
        setSidetoneGainDbLocal={(v) => setSettingsLocal({ sidetoneGainDb: v })}
        setSidetoneGainDbCommit={(v) => commitDebounced({ sidetoneGainDb: v })}
        macros={settings.macros}
        // Pass the current WPM explicitly so a slider change that hasn't
        // round-tripped to the server yet still keys at the operator's
        // intended speed.
        onSend={(macro) => void sendCw(macro, settings.wpm)}
        onAbort={() => void abortCw()}
        onMacroEdit={(i, v) => void setMacro(i, v)}
        onMacroDelete={(i) => void removeMacro(i)}
        onMacroAdd={() => void addMacro()}
        maxMacros={MAX_MACROS}
        status={status}
      />

      {/* GPIO paddle — piHPSDR-style, paddle plugged into the computer.
          Wiring: DOT->GPIO(dot pin), DASH->GPIO(dash pin), common->GND;
          internal pull-ups, contacts active-low. Disabled by default. */}
      <div className="cw-paddle-row">
        <label title="Enable the software iambic keyer fed by a paddle on this computer's GPIO header (Raspberry Pi). The radio's own key jack does not need this.">
          <input
            type="checkbox"
            checked={settings.paddleGpioEnabled}
            onChange={(e) => void patchSettings({ paddleGpioEnabled: e.currentTarget.checked })}
          />
          PADDLE ON GPIO
        </label>
        <label>
          DOT
          <input
            type="number" min={0} max={27} value={settings.paddleDotPin}
            disabled={!settings.paddleGpioEnabled}
            onChange={(e) => void patchSettings({ paddleDotPin: Number(e.currentTarget.value) })}
          />
        </label>
        <label>
          DASH
          <input
            type="number" min={0} max={27} value={settings.paddleDashPin}
            disabled={!settings.paddleGpioEnabled}
            onChange={(e) => void patchSettings({ paddleDashPin: Number(e.currentTarget.value) })}
          />
        </label>
        <label title="Swap dot and dash contacts">
          <input
            type="checkbox"
            checked={settings.paddleSwap}
            disabled={!settings.paddleGpioEnabled}
            onChange={(e) => void patchSettings({ paddleSwap: e.currentTarget.checked })}
          />
          SWAP
        </label>
      </div>
    </div>
  );
}
