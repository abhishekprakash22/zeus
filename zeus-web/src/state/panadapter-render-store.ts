// SPDX-License-Identifier: GPL-2.0-or-later

import { create } from 'zustand';
import { isWebGpuPanadapterEnabled, setWebGpuPanadapterEnabled } from '../gl/webgpu/flag';

type PanadapterRenderState = {
  panadapter3dEnabled: boolean;
  // Set by PanadapterSurface when WebGPU init fails — lets UI toggles show
  // 'unavailable' instead of silently falling back (field: CM5 kiosk).
  pan3dUnavailable: boolean;
  setPan3dUnavailable: (v: boolean) => void;
  setPanadapter3dEnabled: (enabled: boolean) => void;
  togglePanadapterRenderMode: () => void;
};

export const usePanadapterRenderStore = create<PanadapterRenderState>((set, get) => ({
  panadapter3dEnabled: isWebGpuPanadapterEnabled(),
  pan3dUnavailable: false,
  setPan3dUnavailable: (v) => set({ pan3dUnavailable: v }),
  setPanadapter3dEnabled: (enabled) => {
    setWebGpuPanadapterEnabled(enabled);
    set({ panadapter3dEnabled: enabled });
  },
  togglePanadapterRenderMode: () => {
    get().setPanadapter3dEnabled(!get().panadapter3dEnabled);
  },
}));
