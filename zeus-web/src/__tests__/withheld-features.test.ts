// SPDX-License-Identifier: GPL-2.0-or-later
import { describe, expect, it } from 'vitest';
import { CHAT_AVAILABLE, PLUGINS_TAB_AVAILABLE, isPanelWithheld } from '../withheld-features';
import { getAllPanels, getPanelDef } from '../layout/panels';
import { DEFAULT_WORKSPACE_LAYOUT } from '../layout/defaultLayout';
import { parseWorkspaceLayout } from '../layout/workspace';

describe('withheld features', () => {
  it('ships with chat and the plugins tab withheld', () => {
    expect(CHAT_AVAILABLE).toBe(false);
    expect(PLUGINS_TAB_AVAILABLE).toBe(false);
  });

  it('withholds only the chat panel, and only while chat is off', () => {
    expect(isPanelWithheld('chat', false)).toBe(true);
    expect(isPanelWithheld('chat', true)).toBe(false);
    expect(isPanelWithheld('hero', false)).toBe(false);
  });

  it('does not offer Chat in Add Panel or resolve its definition', () => {
    expect(getAllPanels().some((p) => p.id === 'chat')).toBe(false);
    expect(getPanelDef('chat')).toBeUndefined();
    expect(getPanelDef('hero')).toBeDefined();
  });

  it('has no chat tile in the default workspace, and the hero fills its place', () => {
    expect(DEFAULT_WORKSPACE_LAYOUT.tiles.some((t) => t.panelId === 'chat')).toBe(false);
    const hero = DEFAULT_WORKSPACE_LAYOUT.tiles.find((t) => t.panelId === 'hero');
    expect(hero && hero.y + hero.h).toBe(48);
  });

  it('drops a chat tile from a saved layout and keeps the rest', () => {
    const parsed = parseWorkspaceLayout({
      schemaVersion: 8,
      tiles: [
        { uid: 'a', panelId: 'hero', x: 0, y: 0, w: 18, h: 30 },
        { uid: 'b', panelId: 'chat', x: 0, y: 30, w: 18, h: 8 },
        { uid: 'c', panelId: 'some-plugin-panel', x: 18, y: 0, w: 6, h: 8 },
      ],
    });
    expect(parsed.tiles.map((t) => t.uid)).toEqual(['a', 'c']);
  });
});
