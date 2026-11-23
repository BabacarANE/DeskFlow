import { TestBed } from '@angular/core/testing';
import {
  accentContrastWarning,
  contrastRatio,
  isHexColor,
  luminance,
  onColorFor,
} from './contrast';
import { ThemeService } from './theme.service';

describe('contrast helpers', () => {
  it('computes the WCAG ratio between black and white as 21:1', () => {
    expect(contrastRatio('#000000', '#FFFFFF')).toBeCloseTo(21, 5);
  });

  it('is symmetric and 1:1 for identical colours', () => {
    expect(contrastRatio('#7CC4EE', '#7CC4EE')).toBeCloseTo(1, 5);
    expect(contrastRatio('#7CC4EE', '#14212B')).toBeCloseTo(
      contrastRatio('#14212B', '#7CC4EE'),
      10,
    );
  });

  it('computes luminance of pure white as 1', () => {
    expect(luminance('#FFFFFF')).toBeCloseTo(1, 5);
  });

  it('rejects malformed colours', () => {
    expect(isHexColor('#7CC4EE')).toBe(true);
    expect(isHexColor('7CC4EE')).toBe(false);
    expect(isHexColor('#7CC')).toBe(false);
    expect(() => luminance('red')).toThrow();
  });

  it('picks dark text on the sky blue and white text on the amber red (SPEC section 10)', () => {
    expect(onColorFor('#7CC4EE')).toBe('dark');
    expect(onColorFor('#D63A14')).toBe('light');
  });

  it('keeps at least 4.5:1 for the chosen on-colour on the default palette', () => {
    const light = 1.05 / (luminance('#D63A14') + 0.05);
    expect(light).toBeGreaterThanOrEqual(4.5);
  });

  it('flips to white on dark colours and to dark on light colours', () => {
    expect(onColorFor('#101820')).toBe('light');
    expect(onColorFor('#FFE08A')).toBe('dark');
  });

  it('compares with the real dark text colour, not pure black', () => {
    // Mid grey: pure black would win (5.0:1), but the real dark text (about 3.5:1) loses to white (4.2:1).
    const dark = luminance('#0B2A3C');
    expect(onColorFor('#7C7C7C')).toBe('dark');
    expect(onColorFor('#7C7C7C', dark, 1)).toBe('light');
  });

  it('warns when the accent has less than 3:1 against the surface', () => {
    expect(accentContrastWarning('#D63A14', '#F4F7FA')).toBe(false);
    expect(accentContrastWarning('#E8EEF3', '#F4F7FA')).toBe(true);
  });
});

describe('ThemeService', () => {
  const root = document.documentElement;
  const read = (name: string) => root.style.getPropertyValue(name);
  let service: ThemeService;

  beforeEach(() => {
    service = TestBed.inject(ThemeService);
    service.reset();
  });

  afterEach(() => service.reset());

  it('writes the five variables on :root', () => {
    service.apply({ colorPrimary: '#102030', colorSurface: '#FAFAFA', colorAccent: '#FFD400' });
    expect(read('--color-primary')).toBe('#102030');
    expect(read('--color-surface')).toBe('#FAFAFA');
    expect(read('--color-accent')).toBe('#FFD400');
    // Dark primary needs white text, bright yellow accent needs dark text.
    expect(read('--on-primary')).toBe('var(--on-light)');
    expect(read('--on-accent')).toBe('var(--on-dark)');
  });

  it('reads --on-dark from the document to choose the text colour', () => {
    document.documentElement.style.setProperty('--on-dark', '#0B2A3C');
    document.documentElement.style.setProperty('--on-light', '#FFFFFF');
    try {
      service.apply({ colorPrimary: '#7C7C7C', colorSurface: '#FAFAFA', colorAccent: '#FFD400' });
      expect(read('--on-primary')).toBe('var(--on-light)');
    } finally {
      document.documentElement.style.removeProperty('--on-dark');
      document.documentElement.style.removeProperty('--on-light');
    }
  });

  it('uses the default palette contrast choices', () => {
    service.apply({ colorPrimary: '#7CC4EE', colorSurface: '#F4F7FA', colorAccent: '#D63A14' });
    expect(read('--on-primary')).toBe('var(--on-dark)');
    expect(read('--on-accent')).toBe('var(--on-light)');
  });

  it('falls back to the defaults for a missing or malformed theme', () => {
    service.apply({ colorPrimary: '#102030', colorSurface: '#FAFAFA', colorAccent: '#FFD400' });
    service.apply({ colorPrimary: 'blue', colorSurface: '#FAFAFA', colorAccent: '#FFD400' });
    expect(read('--color-primary')).toBe('');
    service.apply({ colorPrimary: '#102030', colorSurface: '#FAFAFA', colorAccent: '#FFD400' });
    service.apply(null);
    expect(read('--color-accent')).toBe('');
    expect(read('--on-accent')).toBe('');
  });

  it('removes every managed variable on reset', () => {
    service.apply({ colorPrimary: '#102030', colorSurface: '#FAFAFA', colorAccent: '#FFD400' });
    service.reset();
    for (const name of [
      '--color-primary',
      '--color-surface',
      '--color-accent',
      '--on-primary',
      '--on-accent',
    ]) {
      expect(read(name)).toBe('');
    }
  });
});
