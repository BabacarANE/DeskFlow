import { DOCUMENT } from '@angular/common';
import { Injectable, inject } from '@angular/core';
import { Theme } from '../api/models';
import { isHexColor, luminance, onColorFor } from './contrast';

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
    style.setProperty('--color-primary', theme.colorPrimary);
    style.setProperty('--color-surface', theme.colorSurface);
    style.setProperty('--color-accent', theme.colorAccent);
    style.setProperty('--on-primary', this.onVariable(theme.colorPrimary));
    style.setProperty('--on-accent', this.onVariable(theme.colorAccent));
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
