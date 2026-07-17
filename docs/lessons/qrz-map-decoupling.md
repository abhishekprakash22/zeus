# QRZ Info Display and Map Decoupling

## Problem

Originally, QRZ callsign information display was tightly coupled to the Leaflet map component. When the map was unavailable (offline, no tile server, Leaflet init failure), the entire QRZ experience would break, even though most QRZ data (name, location, grid, country, license class) is text-based and doesn't depend on the map.

## Solution

The QRZ info panel (`QrzCard`) now renders independently of map availability:

1. **Error boundary**: `LeafletMapErrorBoundary` wraps the `LeafletWorldMap` component and catches initialization or tile-loading failures.

2. **Map availability state**: `mapAvailable` boolean tracks whether the Leaflet map is working. Set to `false` when the error boundary catches a failure.

3. **Conditional map-dependent UI**: Features that require the map (SP/LP/BEAM heading chips, M-modifier for interactive mode, beam visualization) only render when `mapAvailable && terminatorActive && contact`.

4. **Independent QrzCard**: The QRZ info card always renders when QRZ data exists, regardless of map state.

## Load-Bearing Details

- `mapInteractive` now requires `mapAvailable` in addition to `terminatorActive && mapModifier`, preventing the M-key from attempting to interact with a broken map.
- The `contact` object (derived from `qrzStationToContact`) is still created even when the map fails — bearing and distance calculations run but their display is gated on `mapAvailable`.
- When the map is unavailable, the spectrum/waterfall remains fully interactive (no map layer blocking pointer events).

## Testing

To test map-unavailable scenarios:

1. **Offline mode**: Disconnect from the internet and engage QRZ. The info panel should render, but beam chips should disappear.
2. **Tile server failure**: Block `server.arcgisonline.com` in `/etc/hosts` or via browser dev tools. QRZ info should still display.
3. **Leaflet missing**: If the Leaflet library failed to load (CDN issue), the error boundary catches it and QRZ info remains visible.

## Why This Matters

Operators working in offline environments, at remote sites, or with restricted network access still benefit from cached QRZ lookups or manually-entered callsign info. Losing the entire QRZ panel because tile images won't load is poor UX — the text data is the primary value.
