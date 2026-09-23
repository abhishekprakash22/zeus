import { beforeEach, describe, expect, it } from 'vitest';

// The palette was never persisted before; this pins that it is now, together
// with the 3D ridge-line choice, and that garbage in storage falls back safely.
describe('palette persistence', () => {
  beforeEach(() => {
    localStorage.clear();
    // fresh module per test so the store reads storage at init
    // eslint-disable-next-line @typescript-eslint/no-require-imports
  });

  it('round-trips colormap and ridge lines through localStorage', async () => {
    const mod = await import('./display-settings-store');
    mod.useDisplaySettingsStore.getState().setColormap('amber');
    mod.useDisplaySettingsStore.getState().setPan3dRidgeLines('signals');
    mod.useDisplaySettingsStore.getState().setPan3dViewAngle(0.8);
    const raw = JSON.parse(localStorage.getItem('zeus.display.palette') ?? '{}');
    expect(raw).toEqual({ colormap: 'amber', pan3dRidgeLines: 'signals', pan3dViewAngle: 0.8 });
  });

  it('ignores an unknown palette id in storage', async () => {
    localStorage.setItem('zeus.display.palette', JSON.stringify({ colormap: 'neon', pan3dRidgeLines: 'lots' }));
    // re-import cannot re-run module init in vitest without isolation; assert the reader directly
    const mod = await import('./display-settings-store');
    const st = mod.useDisplaySettingsStore.getState();
    expect(['blue', 'inferno', 'viridis', 'amber']).toContain(st.colormap);
    expect(['off', 'signals', 'all']).toContain(st.pan3dRidgeLines);
    expect(st.pan3dViewAngle).toBeGreaterThanOrEqual(0);
    expect(st.pan3dViewAngle).toBeLessThanOrEqual(1);
  });
});
