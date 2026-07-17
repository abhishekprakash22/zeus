// SPDX-License-Identifier: GPL-2.0-or-later
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { probeWebGpu, resetWebGpuProbe } from './caps';

const navigatorDescriptors = new Map<string, PropertyDescriptor | undefined>();

function stubNavigatorProperty(name: string, value: unknown): void {
  if (!navigatorDescriptors.has(name)) {
    navigatorDescriptors.set(name, Object.getOwnPropertyDescriptor(navigator, name));
  }
  Object.defineProperty(navigator, name, {
    configurable: true,
    value,
  });
}

function restoreNavigatorProperties(): void {
  for (const [name, descriptor] of navigatorDescriptors) {
    if (descriptor) {
      Object.defineProperty(navigator, name, descriptor);
    } else {
      Reflect.deleteProperty(navigator, name);
    }
  }
  navigatorDescriptors.clear();
}

function stubWebGpuNavigator(platform: string) {
  const device = {} as GPUDevice;
  const adapter = {
    requestDevice: vi.fn(async () => device),
    info: { vendor: 'test', architecture: 'mock' },
  } as unknown as GPUAdapter;
  const requestAdapter = vi.fn(async () => adapter);
  const gpu = {
    requestAdapter,
    getPreferredCanvasFormat: vi.fn(() => 'bgra8unorm' as GPUTextureFormat),
  } as unknown as GPU;

  stubNavigatorProperty('platform', platform);
  stubNavigatorProperty('gpu', gpu);

  return { requestAdapter };
}

describe('probeWebGpu', () => {
  beforeEach(() => {
    resetWebGpuProbe();
  });

  afterEach(() => {
    resetWebGpuProbe();
    restoreNavigatorProperties();
    vi.restoreAllMocks();
  });

  it('omits powerPreference on Windows to avoid Chromium warning noise', async () => {
    const { requestAdapter } = stubWebGpuNavigator('Win32');

    const result = await probeWebGpu();

    expect(result.supported).toBe(true);
    expect(requestAdapter).toHaveBeenCalledWith(undefined);
  });

  it('requests the high-performance adapter on non-Windows browsers', async () => {
    const { requestAdapter } = stubWebGpuNavigator('Linux x86_64');

    const result = await probeWebGpu();

    expect(result.supported).toBe(true);
    expect(requestAdapter).toHaveBeenCalledWith({ powerPreference: 'high-performance' });
  });
});
