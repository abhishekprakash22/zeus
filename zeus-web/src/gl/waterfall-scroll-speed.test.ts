import { describe, expect, it } from 'vitest';
import { clampScrollSpeed, MAX_SCROLL_SPEED } from './waterfall';
import { WATERFALL_SCROLL_SPEED_MAX } from '../state/display-settings-store';

describe('waterfall scroll speed range', () => {
  it('lets SPD ×4 take effect at the highest global speed', () => {
    expect(MAX_SCROLL_SPEED).toBeGreaterThanOrEqual(WATERFALL_SCROLL_SPEED_MAX * 4);
    expect(clampScrollSpeed(4)).toBe(4);
    expect(clampScrollSpeed(WATERFALL_SCROLL_SPEED_MAX * 4)).toBe(WATERFALL_SCROLL_SPEED_MAX * 4);
  });
  it('keeps the history-depth floor and rejects junk', () => {
    expect(clampScrollSpeed(0.1)).toBe(0.25);
    expect(clampScrollSpeed(Number.NaN)).toBe(1);
    expect(clampScrollSpeed(1000)).toBe(MAX_SCROLL_SPEED);
  });
});
