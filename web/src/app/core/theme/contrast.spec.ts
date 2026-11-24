import {
  ACCENT_CONTRAST_WARNING,
  MIN_TEXT_CONTRAST,
  accentContrastWarning,
  chosenTextContrast,
  contrastRatio,
  luminance,
  normalizeHex,
  onColorFor,
} from './contrast';

describe('contrast (phase 4)', () => {
  it('always reaches 4.5:1 with the better of black and white text', () => {
    // A coarse sweep of the RGB cube: the text colour chosen for any background must read.
    const steps = [0, 51, 102, 153, 204, 255];
    const hex = (n: number) => n.toString(16).padStart(2, '0');
    for (const r of steps) {
      for (const g of steps) {
        for (const b of steps) {
          const background = `#${hex(r)}${hex(g)}${hex(b)}`;
          const text = onColorFor(background) === 'dark' ? '#000000' : '#FFFFFF';
          expect(contrastRatio(background, text), background).toBeGreaterThanOrEqual(4.5);
        }
      }
    }
  });

  it('reports mid-tones the real dark text colour can not serve at 4.5:1', () => {
    const dark = luminance('#0B2A3C');
    expect(chosenTextContrast('#7C7C7C')).toBeGreaterThanOrEqual(MIN_TEXT_CONTRAST);
    expect(chosenTextContrast('#7C7C7C', dark, 1)).toBeLessThan(MIN_TEXT_CONTRAST);
    expect(chosenTextContrast('#7CC4EE', dark, 1)).toBeGreaterThanOrEqual(MIN_TEXT_CONTRAST);
  });

  it('flags a pale accent on a pale surface and accepts the default pair', () => {
    expect(accentContrastWarning('#F0E68C', '#F4F7FA')).toBe(true);
    expect(accentContrastWarning('#D63A14', '#F4F7FA')).toBe(false);
    expect(ACCENT_CONTRAST_WARNING).toContain('3:1');
  });

  it('flips the warning around the 3:1 limit', () => {
    // #767676 on white is 4.54:1, #949494 on white is 3.03:1, #A0A0A0 is 2.6:1.
    expect(accentContrastWarning('#949494', '#FFFFFF')).toBe(false);
    expect(accentContrastWarning('#A0A0A0', '#FFFFFF')).toBe(true);
  });

  it('normalises hex colours to upper case', () => {
    expect(normalizeHex(' #7cc4ee ')).toBe('#7CC4EE');
  });
});
