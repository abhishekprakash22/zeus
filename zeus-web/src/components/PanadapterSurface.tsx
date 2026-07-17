// SPDX-License-Identifier: GPL-2.0-or-later
//
// Renderer switch for the panadapter surface. WebGPU 3D is the default when the
// operator/device allows it; the existing WebGL2 panadapter remains the fallback
// for unsupported GPU stacks or `?webgpuPanadapter=0`.

import { useEffect, useState, type ComponentProps } from 'react';
import { usePanadapterRenderStore } from '../state/panadapter-render-store';
import { Panadapter } from './Panadapter';
import { Panadapter3D } from './Panadapter3D';

type PanadapterProps = NonNullable<ComponentProps<typeof Panadapter>>;

export function PanadapterSurface(props: PanadapterProps) {
  const [pan3dUnavailable, setPan3dUnavailable] = useState(false);
  const panadapter3dEnabled = usePanadapterRenderStore((s) => s.panadapter3dEnabled);
  const usePan3d = panadapter3dEnabled && !pan3dUnavailable;

  useEffect(() => {
    setPan3dUnavailable(false);
  }, [panadapter3dEnabled]);

  if (usePan3d) {
    return <Panadapter3D {...props} onUnavailable={() => setPan3dUnavailable(true)} />;
  }
  return <Panadapter {...props} />;
}
