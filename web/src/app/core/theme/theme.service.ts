import { DOCUMENT } from '@angular/common';
import { Injectable, inject } from '@angular/core';
import { Theme } from '../api/models';
import { OnLuminances, chooseOnColor, contrastRatio, isHexColor, luminance } from './contrast';

const MANAGED_PROPERTIES = [
  '--color-primary',
  '--color-surface',
  '--color-accent',
  '--on-primary',
  '--on-accent',
];

/** Writes the tenant theme as CSS variables on :root (SPEC section 10). */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly root = inject(DOCUMENT).documentElement;

  /** Applies a theme; an absent or malformed theme falls back to the default palette. */
  apply(theme: Theme | null | undefined): void {
    if (
      !theme ||
      !isHexColor(theme.colorPrimary) ||
      !isHexColor(theme.colorSurface) ||
      !isHexColor(theme.colorAccent)
    ) {
      this.reset();
      return;
    }
    const style = this.root.style;
    for (const [name, value] of Object.entries(this.variablesFor(theme))) {
      style.setProperty(name, value);
    }
  }

  /**
   * The five managed variables for a valid theme. Also used by the theme preview, which writes
   * them on its own container instead of :root.
   */
  variablesFor(theme: Theme): Record<string, string> {
    return {
      '--color-primary': theme.colorPrimary,
      '--color-surface': theme.colorSurface,
      '--color-accent': theme.colorAccent,
      '--on-primary': this.onVariable(theme.colorPrimary),
      '--on-accent': this.onVariable(theme.colorAccent),
    };
  }

  /** Contrast of the text colour that `--on-*` would use on a background (same rule as apply). */
  textContrast(background: string): number {
    return chooseOnColor(background, this.luminances()).ratio;
  }

  /** Contrast of the main text colour (--color-ink) on a surface, or null when it is unknown. */
  inkContrast(surface: string): number | null {
    const ink = getComputedStyle(this.root).getPropertyValue('--color-ink').trim();
    return isHexColor(ink) ? contrastRatio(ink, surface) : null;
  }

  reset(): void {
    for (const property of MANAGED_PROPERTIES) this.root.style.removeProperty(property);
  }

  private onVariable(background: string): string {
    return `var(--on-${chooseOnColor(background, this.luminances()).choice})`;
  }

  private luminances(): OnLuminances {
    return {
      dark: this.luminanceOf('--on-dark', 0),
      light: this.luminanceOf('--on-light', 1),
      darkest: this.luminanceOf('--on-darkest', 0),
    };
  }

  /** Luminance of a token defined in tokens.css, so the choice uses the real text colours. */
  private luminanceOf(token: string, fallback: number): number {
    const value = getComputedStyle(this.root).getPropertyValue(token).trim();
    return isHexColor(value) ? luminance(value) : fallback;
  }
}
