// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// FT8/FT4 as a workspace panel.
//
// The digital window already floats and drags anywhere in the viewport, but
// position:fixed can never leave the browser window — so it could not be put on
// a second monitor. Registering the same body as a PANEL means it inherits the
// whole tile mechanism instead: place it, size it, lock it, and "Send to second
// screen", which moves the tile into the shared Second Screen layout for a
// detached window on another display. One React tree, one set of stores, no
// second document to bridge.
//
// The floating pop-out is unchanged and still available — this is an
// additional home for the same content, not a replacement, so nobody's
// existing habit breaks.

import { Ft8PopBody } from '../ft8/Ft8PopBody';
import '../../styles/ft8-theme.css';

export function Ft8Panel() {
  // .digital-window is the token LAYER the whole ft8-theme.css hangs off (it
  // defines --hud-* and has a light-theme override), so the panel must carry
  // it or every colour inside falls back. .dw-content supplies the flex column
  // + background the body expects; the tile provides the chrome the pop-out
  // would otherwise draw itself.
  return (
    <div
      className="digital-window"
      style={{
        height: '100%',
        width: '100%',
        minHeight: 0,
        display: 'flex',
        flexDirection: 'column',
        // The pop-out is position:fixed with its own size; in a tile the tile
        // owns geometry, so neither is inherited here.
        position: 'static',
      }}
    >
      <div className="dw-content">
        <Ft8PopBody />
      </div>
    </div>
  );
}
