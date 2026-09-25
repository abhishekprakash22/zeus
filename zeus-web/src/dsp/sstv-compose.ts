// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// SSTV picture composition for the send panel: draw the outgoing picture at
// the mode's resolution, and turn canvas pixels into the transmitter's input.

/** Draw the outgoing picture: the source cover-fitted, then the text lines
 *  in white with a dark outline (legible on any picture, and the look SSTV
 *  operators expect). */
export function composeSstvPicture(
  ctx: CanvasRenderingContext2D,
  w: number,
  h: number,
  src: (CanvasImageSource & { width: number; height: number }) | null,
  top: string,
  bottom: string,
): void {
  ctx.fillStyle = 'black';
  ctx.fillRect(0, 0, w, h);
  if (src && src.width > 0 && src.height > 0) {
    const k = Math.max(w / src.width, h / src.height);
    const dw = src.width * k,
      dh = src.height * k;
    ctx.drawImage(src, (w - dw) / 2, (h - dh) / 2, dw, dh);
  }
  const size = Math.round(h / 9);
  ctx.font = `bold ${size}px sans-serif`;
  ctx.lineJoin = 'round';
  ctx.lineWidth = Math.max(2, Math.round(size / 7));
  ctx.strokeStyle = 'black';
  ctx.fillStyle = 'white';
  const text = (s: string, x: number, y: number, align: CanvasTextAlign) => {
    if (!s) return;
    ctx.textAlign = align;
    ctx.strokeText(s, x, y, w * 0.94);
    ctx.fillText(s, x, y, w * 0.94);
  };
  text(top, Math.round(w * 0.04), Math.round(h * 0.04 + size * 0.85), 'left');
  text(bottom, Math.round(w / 2), Math.round(h * 0.95), 'center');
}

/** RGBA pixels → base64 RGB, the transmitter's input. */
export function rgbaToRgbBase64(rgba: Uint8ClampedArray): string {
  const n = rgba.length / 4;
  const rgb = new Uint8Array(n * 3);
  for (let i = 0; i < n; i++) {
    rgb[3 * i] = rgba[4 * i]!;
    rgb[3 * i + 1] = rgba[4 * i + 1]!;
    rgb[3 * i + 2] = rgba[4 * i + 2]!;
  }
  let bin = '';
  const CHUNK = 0x8000;
  for (let i = 0; i < rgb.length; i += CHUNK)
    bin += String.fromCharCode(...rgb.subarray(i, i + CHUNK));
  return btoa(bin);
}
