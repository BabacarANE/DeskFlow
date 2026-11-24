import { DOCUMENT } from '@angular/common';
import { Injectable, inject } from '@angular/core';
import { Theme } from '../api/models';
import { chosenTextContrast, isHexColor, luminance, onColorFor } from './contrast';

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

  /** Contrast of the text colour (--on-dark or --on-light) that `--on-*` would use on a background. */
  textContrast(background: string): number {
    return chosenTextContrast(
      background,
      this.luminanceOf('--on-dark', 0),
      this.luminanceOf('--on-light', 1),
    );
  }

  reset(): void {
    for (const property of MANAGED_PROPERTIES) this.root.style.removeProperty(property);
  }

  private onVariable(background: string): string {
    const dark = this.luminanceOf('--on-dark', 0);
    const light = this.luminanceOf('--on-light', 1);
    return onColorFor(background, dark, light) === 'dark' ? 'var(--on-dark)' : 'var(--on-light)';
  }

  /** Luminance of a token defined in tokens.css, so the choice uses the real text colours. */
  private luminanceOf(token: string, fallback: number): number {
    const value = getComputedStyle(this.root).getPropertyValue(token).trim();
    return isHexColor(value) ? luminance(value) : fallback;
  }
}
