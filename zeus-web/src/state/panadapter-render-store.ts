// SPDX-License-Identifier: GPL-2.0-or-later

import { create } from 'zustand';
import { isWebGpuPanadapterEnabled, setWebGpuPanadapterEnabled } from '../gl/webgpu/flag';

type PanadapterRenderState = {
  panadapter3dEnabled: boolean;
  setPanadapter3dEnabled: (enabled: boolean) => void;
  togglePanadapterRenderMode: () => void;
};

export const usePanadapterRenderStore = create<PanadapterRenderState>((set, get) => ({
  panadapter3dEnabled: isWebGpuPanadapterEnabled(),
  setPanadapter3dEnabled: (enabled) => {
    setWebGpuPanadapterEnabled(enabled);
    set({ panadapter3dEnabled: enabled });
  },
  togglePanadapterRenderMode: () => {
    get().setPanadapter3dEnabled(!get().panadapter3dEnabled);
  },
}));
