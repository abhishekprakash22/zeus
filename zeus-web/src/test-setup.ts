// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Vitest setup. Tells React 19 that this is an act(...) environment so
// component tests using React's bundled `act` don't log noisy warnings.

// Newer Node releases expose a localStorage global only when a backing file is
// configured. jsdom tests and Zustand persist stores expect the Storage API to
// be usable, so install a small in-memory implementation before test modules
// import stores that capture localStorage.
(() => {
  const descriptor = Object.getOwnPropertyDescriptor(globalThis, 'localStorage');
  const existing = descriptor && 'value' in descriptor
    ? descriptor.value as Storage | undefined
    : undefined;
  if (existing && typeof existing.setItem === 'function') {
    try {
      existing.setItem('__zeus-test', '1');
      existing.removeItem('__zeus-test');
      return;
    } catch {
      // fall through and install the test implementation
    }
  }

  const store = new Map<string, string>();
  const polyfill: Storage = {
    get length() {
      return store.size;
    },
    clear() {
      store.clear();
    },
    getItem(key) {
      return store.has(key) ? (store.get(key) as string) : null;
    },
    key(index) {
      return Array.from(store.keys())[index] ?? null;
    },
    removeItem(key) {
      store.delete(key);
    },
    setItem(key, value) {
      store.set(key, String(value));
    },
  };

  Object.defineProperty(globalThis, 'localStorage', {
    configurable: true,
    value: polyfill,
  });

  if (typeof window !== 'undefined') {
    Object.defineProperty(window, 'localStorage', {
      configurable: true,
      value: polyfill,
    });
  }
})();

// React 19 wants the host environment to advertise act-readiness via this
// magic global. Avoid `declare global { var ... }` (lint complains about
// implicit `var` redeclaration) and reach through `globalThis` directly.
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

export {};
