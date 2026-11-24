import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError } from '../../core/api/api-error';
import { Theme } from '../../core/api/models';
import { ThemeService } from '../../core/theme/theme.service';
import { ThemeSettings } from './theme-settings';

const DEFAULTS: Theme = {
  colorPrimary: '#7CC4EE',
  colorSurface: '#F4F7FA',
  colorAccent: '#D63A14',
};

describe('ThemeSettings', () => {
  const api = { getTheme: vi.fn(), updateTheme: vi.fn(), resetTheme: vi.fn() };
  const root = document.documentElement;
  let fixture: ComponentFixture<ThemeSettings>;
  const el = () => fixture.nativeElement as HTMLElement;
  const hex = (name: string) => el().querySelector(`#theme-${name}`) as HTMLInputElement;
  const preview = () => el().querySelector('.theme-scope') as HTMLElement;
  const button = (text: string) =>
    Array.from(el().querySelectorAll('button')).find((b) => b.textContent?.includes(text));
  const rootVar = (name: string) => root.style.getPropertyValue(name);

  function type(name: string, value: string) {
    hex(name).value = value;
    hex(name).dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  async function settle() {
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve));
    fixture.detectChanges();
  }

  async function render() {
    fixture = TestBed.createComponent(ThemeSettings);
    fixture.detectChanges();
    await settle();
  }

  beforeEach(() => {
    Object.values(api).forEach((fn) => fn.mockReset());
    api.getTheme.mockReturnValue(of(DEFAULTS));
    TestBed.configureTestingModule({ providers: [{ provide: ApiClient, useValue: api }] });
    TestBed.inject(ThemeService).reset();
  });

  afterEach(() => TestBed.inject(ThemeService).reset());

  it('shows the exact instruction and loads the current colours', async () => {
    await render();
    expect(el().textContent).toContain(
      'Choisissez deux couleurs discrètes et une couleur qui claque.',
    );
    expect(hex('colorPrimary').value).toBe('#7CC4EE');
    expect(hex('colorSurface').value).toBe('#F4F7FA');
    expect(hex('colorAccent').value).toBe('#D63A14');
  });

  it('offers a native colour picker and a labelled hex field for each colour', async () => {
    await render();
    const pickers = el().querySelectorAll('input[type="color"]');
    expect(pickers).toHaveLength(3);
    for (const picker of Array.from(pickers)) {
      expect(picker.getAttribute('aria-label')).toContain('Sélecteur de couleur');
    }
    for (const name of ['colorPrimary', 'colorSurface', 'colorAccent']) {
      expect(el().querySelector(`label[for="theme-${name}"]`), name).not.toBeNull();
    }
    expect(el().querySelectorAll('fieldset')).toHaveLength(3);
  });

  it('keeps the picker and the hex field in sync, both ways', async () => {
    await render();
    const picker = el().querySelectorAll('input[type="color"]')[2] as HTMLInputElement;
    picker.value = '#112233';
    picker.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(hex('colorAccent').value).toBe('#112233'.toUpperCase());

    type('colorAccent', '#aa5500');
    expect(picker.value).toBe('#aa5500');
  });

  it('previews live on its own container and never on :root before saving', async () => {
    await render();
    expect(preview().style.getPropertyValue('--color-accent')).toBe('#D63A14');
    type('colorPrimary', '#102030');
    type('colorAccent', '#FFD400');
    // The preview container follows the form, with its own text colours...
    expect(preview().style.getPropertyValue('--color-primary')).toBe('#102030');
    expect(preview().style.getPropertyValue('--color-accent')).toBe('#FFD400');
    expect(preview().style.getPropertyValue('--on-primary')).toBe('var(--on-light)');
    expect(preview().style.getPropertyValue('--on-accent')).toBe('var(--on-dark)');
    // ...while the application (:root) is untouched.
    for (const name of ['--color-primary', '--color-surface', '--color-accent', '--on-primary']) {
      expect(rootVar(name), name).toBe('');
    }
    expect(api.updateTheme).not.toHaveBeenCalled();
  });

  it('shows a mini ticket list and an action button in the preview', async () => {
    await render();
    expect(preview().querySelectorAll('li').length).toBeGreaterThanOrEqual(3);
    expect(preview().querySelector('.btn-accent')).not.toBeNull();
    expect(el().querySelector('#theme-preview-title')?.textContent).toContain('Aperçu en direct');
  });

  it('flags a malformed colour, links the message to the field and keeps the last valid preview', async () => {
    await render();
    type('colorPrimary', '#12');
    expect(hex('colorPrimary').parentElement?.textContent).toContain('#RRGGBB');
    expect(hex('colorPrimary').getAttribute('aria-describedby')).toBe('theme-colorPrimary-error');
    expect(preview().style.getPropertyValue('--color-primary')).toBe('#7CC4EE');
    const picker = el().querySelectorAll('input[type="color"]')[0] as HTMLInputElement;
    expect(picker.value).toBe('#7cc4ee');

    button('Enregistrer le thème')?.click();
    await settle();
    expect(api.updateTheme).not.toHaveBeenCalled();
  });

  it('warns, without blocking, when the accent has less than 3:1 against the surface', async () => {
    await render();
    expect(el().querySelector('[aria-label="Avertissements"]')).toBeNull();
    type('colorAccent', '#E8EEF3');
    const warning = el().querySelector('[aria-label="Avertissements"]');
    expect(warning?.getAttribute('role')).toBe('status');
    expect(warning?.textContent).toContain('inférieur à 3:1');
    expect((button('Enregistrer le thème') as HTMLButtonElement).disabled).toBe(false);
    type('colorAccent', '#D63A14');
    expect(el().querySelector('[aria-label="Avertissements"]')).toBeNull();
  });

  it('warns when no text colour reaches 4.5:1 on a mid-tone with the real dark colour', async () => {
    root.style.setProperty('--on-dark', '#0B2A3C');
    root.style.setProperty('--on-light', '#FFFFFF');
    try {
      await render();
      type('colorPrimary', '#7C7C7C');
      expect(el().querySelector('[aria-label="Avertissements"]')?.textContent).toContain(
        'couleur principale',
      );
    } finally {
      root.style.removeProperty('--on-dark');
      root.style.removeProperty('--on-light');
    }
  });

  it('saves upper-case colours and applies the theme to the whole application at once', async () => {
    const saved: Theme = {
      colorPrimary: '#102030',
      colorSurface: '#FAFAFA',
      colorAccent: '#FFD400',
    };
    api.updateTheme.mockReturnValue(of({ theme: saved, warnings: [] }));
    await render();
    type('colorPrimary', '#102030');
    type('colorSurface', '#fafafa');
    type('colorAccent', '#ffd400');
    button('Enregistrer le thème')?.click();
    await settle();

    expect(api.updateTheme).toHaveBeenCalledWith(saved);
    expect(rootVar('--color-primary')).toBe('#102030');
    expect(rootVar('--color-surface')).toBe('#FAFAFA');
    expect(rootVar('--color-accent')).toBe('#FFD400');
    expect(rootVar('--on-primary')).toBe('var(--on-light)');
    expect(rootVar('--on-accent')).toBe('var(--on-dark)');
    expect(el().textContent).toContain('Thème enregistré');
  });

  it('shows the warnings returned by the API after saving', async () => {
    const saved: Theme = { ...DEFAULTS, colorAccent: '#E8EEF3' };
    api.updateTheme.mockReturnValue(
      of({ theme: saved, warnings: ['Contraste insuffisant (serveur).'] }),
    );
    await render();
    type('colorAccent', '#E8EEF3');
    button('Enregistrer le thème')?.click();
    await settle();
    expect(el().querySelector('[aria-label="Avertissements"]')?.textContent).toContain(
      'Contraste insuffisant (serveur).',
    );
  });

  it('shows 422 errors under the matching colour', async () => {
    api.updateTheme.mockReturnValue(
      throwError(
        () =>
          new ApiError(422, 'Certaines informations sont invalides.', null, {
            colorAccent: ['Couleur refusée.'],
          }),
      ),
    );
    await render();
    type('colorAccent', '#123456');
    button('Enregistrer le thème')?.click();
    await settle();
    expect(hex('colorAccent').parentElement?.textContent).toContain('Couleur refusée.');
    expect(rootVar('--color-accent')).toBe('');
  });

  it('resets only after a confirmation, then applies the default palette', async () => {
    api.getTheme.mockReturnValue(of({ ...DEFAULTS, colorPrimary: '#102030' }));
    api.resetTheme.mockReturnValue(of(DEFAULTS));
    await render();
    TestBed.inject(ThemeService).apply({ ...DEFAULTS, colorPrimary: '#102030' });
    expect(rootVar('--color-primary')).toBe('#102030');

    button('Réinitialiser')?.click();
    fixture.detectChanges();
    expect(api.resetTheme).not.toHaveBeenCalled();
    expect(button('Confirmer la réinitialisation')).toBeDefined();

    // Cancelling leaves everything as it was.
    button('Annuler')?.click();
    fixture.detectChanges();
    expect(button('Confirmer la réinitialisation')).toBeUndefined();
    expect(api.resetTheme).not.toHaveBeenCalled();

    button('Réinitialiser')?.click();
    fixture.detectChanges();
    button('Confirmer la réinitialisation')?.click();
    await settle();
    expect(api.resetTheme).toHaveBeenCalledTimes(1);
    expect(rootVar('--color-primary')).toBe('#7CC4EE');
    expect(hex('colorPrimary').value).toBe('#7CC4EE');
    expect(el().textContent).toContain('Palette par défaut rétablie.');
  });

  it('shows an error state with a retry button when the theme cannot be loaded', async () => {
    api.getTheme.mockReturnValueOnce(
      throwError(() => new ApiError(500, 'Le serveur a rencontré une erreur.', null)),
    );
    await render();
    expect(el().querySelector('[role="alert"]')?.textContent).toContain('serveur');
    (el().querySelector('[role="alert"] button') as HTMLButtonElement).click();
    await settle();
    expect(hex('colorPrimary').value).toBe('#7CC4EE');
  });

  it('has exactly one accent button for the form (the preview one is an illustration)', async () => {
    await render();
    const interactive = Array.from(el().querySelectorAll('button.btn-accent'));
    expect(interactive).toHaveLength(1);
    expect(preview().querySelector('button')).toBeNull();
    expect(preview().querySelector('[aria-hidden="true"]')).not.toBeNull();
  });
});
