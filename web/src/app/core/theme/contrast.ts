// WCAG 2.x relative luminance and contrast helpers. Operates on runtime colour strings only.

const HEX_COLOR = /^#[0-9A-Fa-f]{6}$/;

export function isHexColor(value: string): boolean {
  return HEX_COLOR.test(value);
}

function channel(value: number): number {
  const c = value / 255;
  return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
}

/** Relative luminance (0 to 1) of a "#RRGGBB" colour. */
export function luminance(hex: string): number {
  if (!isHexColor(hex)) throw new Error(`Invalid colour: ${hex}`);
  const r = channel(parseInt(hex.slice(1, 3), 16));
  const g = channel(parseInt(hex.slice(3, 5), 16));
  const b = channel(parseInt(hex.slice(5, 7), 16));
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

export function contrastRatio(a: string, b: string): number {
  const la = luminance(a);
  const lb = luminance(b);
  return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05);
}

/** Contrast ratio between a background and pure white or pure black text. */
export function contrastWithWhite(background: string): number {
  return 1.05 / (luminance(background) + 0.05);
}

export function contrastWithBlack(background: string): number {
  return (luminance(background) + 0.05) / 0.05;
}

/** Text on this background should be dark ("dark") or white ("light"), whichever reads better. */
export function onColorFor(background: string): 'dark' | 'light' {
  return contrastWithWhite(background) >= contrastWithBlack(background) ? 'light' : 'dark';
}

/** Non-blocking warning: the accent should keep at least 3:1 against the surface. */
export function accentContrastWarning(accent: string, surface: string): boolean {
  return contrastRatio(accent, surface) < 3;
}
