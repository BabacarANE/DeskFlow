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

/** Contrast ratio between a background and a text colour given by its relative luminance. */
function contrastWithLuminance(background: string, textLuminance: number): number {
  const a = luminance(background) + 0.05;
  const b = textLuminance + 0.05;
  return Math.max(a, b) / Math.min(a, b);
}

/**
 * Text on this background should be dark ("dark") or light ("light"), whichever reads better.
 * Luminances are those of the real --on-dark and --on-light values (defaults: black and white).
 */
export function onColorFor(
  background: string,
  darkLuminance = 0,
  lightLuminance = 1,
): 'dark' | 'light' {
  return contrastWithLuminance(background, lightLuminance) >=
    contrastWithLuminance(background, darkLuminance)
    ? 'light'
    : 'dark';
}

/** Non-blocking warning: the accent should keep at least 3:1 against the surface. */
export function accentContrastWarning(accent: string, surface: string): boolean {
  return contrastRatio(accent, surface) < 3;
}
