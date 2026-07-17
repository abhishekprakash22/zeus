// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.

const WORKSPACE_WINDOW_PARAM = 'workspaceWindow';
const WORKSPACE_LAYOUT_PARAM = 'layout';
const SETTINGS_WINDOW_PARAM = 'settingsWindow';
const AUDIO_SUITE_WINDOW_PARAM = 'audioSuiteWindow';

// The detached Audio Suite window opens at the same footprint the in-app
// floating suite used (DEFAULT_WIDTH/DEFAULT_HEIGHT in audio-suite-store),
// so popping it out doesn't resize the operator's rack. Keep these in sync
// with that store.
export const AUDIO_SUITE_WINDOW_WIDTH = 860;
export const AUDIO_SUITE_WINDOW_HEIGHT = 760;

export type AudioSuiteWindowRoute = 'tx' | 'rx';

interface PhotinoExternal {
  sendMessage?: (message: string) => void;
}

interface PhotinoWindowSurface {
  external?: PhotinoExternal;
}

export function detachedWorkspaceUrl(layoutId: string): string {
  const url = new URL(window.location.href);
  url.searchParams.delete(SETTINGS_WINDOW_PARAM);
  url.searchParams.delete(AUDIO_SUITE_WINDOW_PARAM);
  url.searchParams.set(WORKSPACE_WINDOW_PARAM, '1');
  url.searchParams.set(WORKSPACE_LAYOUT_PARAM, layoutId);
  url.hash = '';
  return url.toString();
}

export function detachedSettingsUrl(): string {
  const url = new URL(window.location.href);
  url.searchParams.delete(WORKSPACE_WINDOW_PARAM);
  url.searchParams.delete(WORKSPACE_LAYOUT_PARAM);
  url.searchParams.delete(AUDIO_SUITE_WINDOW_PARAM);
  url.searchParams.set(SETTINGS_WINDOW_PARAM, '1');
  url.hash = '';
  return url.toString();
}

export function detachedAudioSuiteUrl(route: AudioSuiteWindowRoute): string {
  const url = new URL(window.location.href);
  url.searchParams.delete(WORKSPACE_WINDOW_PARAM);
  url.searchParams.delete(WORKSPACE_LAYOUT_PARAM);
  url.searchParams.delete(SETTINGS_WINDOW_PARAM);
  url.searchParams.set(AUDIO_SUITE_WINDOW_PARAM, route);
  url.hash = '';
  return url.toString();
}

export function currentDetachedWorkspaceLayoutId(): string | null {
  const sp = new URLSearchParams(window.location.search);
  return sp.get(WORKSPACE_WINDOW_PARAM) === '1'
    ? sp.get(WORKSPACE_LAYOUT_PARAM)
    : null;
}

export function isDetachedSettingsWindow(): boolean {
  const sp = new URLSearchParams(window.location.search);
  return sp.get(SETTINGS_WINDOW_PARAM) === '1';
}

export function currentDetachedAudioSuiteRoute(): AudioSuiteWindowRoute | null {
  const sp = new URLSearchParams(window.location.search);
  const value = sp.get(AUDIO_SUITE_WINDOW_PARAM);
  return value === 'tx' || value === 'rx' ? value : null;
}

/** True when running inside the Photino desktop shell (the host bridge that
 *  can open real OS windows). Detached "windows" only persist/restore there;
 *  in a plain browser they'd be popups, which the restore path must not spawn
 *  on load. */
export function isDesktopShell(): boolean {
  const external = (window as unknown as PhotinoWindowSurface).external;
  return typeof external?.sendMessage === 'function';
}

interface PersistedWorkspaceWindow {
  layoutId: string;
  title: string;
}

/** Reopen the detached workspace windows the operator left open at the last
 *  desktop shutdown. Desktop shell only (see isDesktopShell); a no-op in the
 *  browser and remote clients. Best-effort — a fetch failure just means nothing
 *  is restored. Call once, from the MAIN window only. */
export async function restorePersistedWorkspaceWindows(): Promise<void> {
  if (!isDesktopShell()) return;
  try {
    const res = await fetch('/api/ui/workspace-windows');
    if (!res.ok) return;
    const list = (await res.json()) as PersistedWorkspaceWindow[];
    if (!Array.isArray(list)) return;
    for (const w of list) {
      if (w?.layoutId) {
        openWorkspaceWindow(w.layoutId, w.title || 'Workspace');
      }
    }
  } catch {
    // Best-effort restore — never block app startup on it.
  }
}

export function openWorkspaceWindow(layoutId: string, title: string): void {
  const url = detachedWorkspaceUrl(layoutId);
  const external = (window as unknown as PhotinoWindowSurface).external;
  const sendMessage = external?.sendMessage;
  if (typeof sendMessage === 'function') {
    sendMessage(JSON.stringify({
      type: 'zeus.openWorkspaceWindow',
      layoutId,
      title,
      url,
    }));
    return;
  }

  window.open(
    url,
    `zeus-workspace-${layoutId}`,
    'popup,width=1180,height=760,noopener,noreferrer',
  );
}

export function openSettingsWindow(): void {
  const url = detachedSettingsUrl();
  const external = (window as unknown as PhotinoWindowSurface).external;
  const sendMessage = external?.sendMessage;
  if (typeof sendMessage === 'function') {
    sendMessage(JSON.stringify({
      type: 'zeus.openSettingsWindow',
      title: 'Settings',
      url,
    }));
    return;
  }

  window.open(
    url,
    'zeus-settings',
    'popup,width=1180,height=760,noopener,noreferrer',
  );
}

/**
 * Open the TX or RX Audio Suite in its own independent OS window — the way a
 * hosted VST plugin's editor pops out, detached from the main Zeus window.
 * On the desktop shell the Photino host opens a real child window (the webview
 * swallows window.open); a plain browser gets a popup of the same footprint.
 * Fire-and-forget: the operator closes it via the window chrome, so there is no
 * open/closed state to track in the main window's store.
 */
export function openAudioSuiteWindow(route: AudioSuiteWindowRoute): void {
  const url = detachedAudioSuiteUrl(route);
  const title = route === 'rx' ? 'RX Audio Suite' : 'TX Audio Suite';
  const external = (window as unknown as PhotinoWindowSurface).external;
  const sendMessage = external?.sendMessage;
  if (typeof sendMessage === 'function') {
    sendMessage(JSON.stringify({
      type: 'zeus.openAudioSuiteWindow',
      route,
      title,
      url,
    }));
    return;
  }

  window.open(
    url,
    `zeus-audio-suite-${route}`,
    `popup,width=${AUDIO_SUITE_WINDOW_WIDTH},height=${AUDIO_SUITE_WINDOW_HEIGHT},noopener,noreferrer`,
  );
}
