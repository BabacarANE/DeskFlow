import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  AbstractControl,
  NonNullableFormBuilder,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError, errorMessage } from '../../core/api/api-error';
import { Theme } from '../../core/api/models';
import {
  ACCENT_CONTRAST_WARNING,
  MIN_TEXT_CONTRAST,
  accentContrastWarning,
  contrastRatio,
  isHexColor,
  normalizeHex,
} from '../../core/theme/contrast';
import { ThemeService } from '../../core/theme/theme.service';
import { FieldError, applyFieldErrors, describedBy } from '../../shared/field-error';
import { StatusMessage } from '../../shared/status-message';

type ThemeField = keyof Theme;

interface ColorSpec {
  field: ThemeField;
  label: string;
  hint: string;
}

const COLORS: ColorSpec[] = [
  { field: 'colorPrimary', label: 'Couleur principale', hint: 'Discrète : barre de navigation.' },
  { field: 'colorSurface', label: 'Couleur de fond', hint: 'Discrète : fond de l’application.' },
  {
    field: 'colorAccent',
    label: 'Couleur d’accent',
    hint: 'Qui claque : bouton principal et alertes.',
  },
];

function formatRatio(ratio: number): string {
  return `${ratio.toFixed(1).replace('.', ',')}:1`;
}

function hexValidator(control: AbstractControl): ValidationErrors | null {
  return isHexColor(String(control.value).trim())
    ? null
    : { custom: { message: 'Format attendu : #RRGGBB (un dièse et six chiffres hexadécimaux).' } };
}

/**
 * Theme tab (US-19): three colours, live preview scoped to its own container, non-blocking
 * warnings, reset. The theme only reaches :root, through ThemeService, once it is saved.
 */
@Component({
  selector: 'app-theme-settings',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, FieldError, StatusMessage],
  template: `
    <section class="card space-y-5" aria-labelledby="theme-title">
      <h2 id="theme-title" class="text-xl font-semibold">Thème de l’entreprise</h2>
      <p id="theme-instruction" class="font-medium">
        Choisissez deux couleurs discrètes et une couleur qui claque.
      </p>

      @switch (status()) {
        @case ('loading') {
          <app-status-message kind="loading" message="Chargement du thème…" />
        }
        @case ('error') {
          <app-status-message kind="error" [message]="loadError()" (retry)="load()" />
        }
        @default {
          <div class="grid gap-6 lg:grid-cols-2">
            <form
              [formGroup]="form"
              (ngSubmit)="save()"
              novalidate
              class="space-y-5"
              aria-describedby="theme-instruction"
            >
              @for (spec of colors; track spec.field) {
                <fieldset class="space-y-2">
                  <legend class="text-sm font-medium">{{ spec.label }}</legend>
                  <p class="text-sm text-ink-muted">{{ spec.hint }}</p>
                  <div class="flex flex-wrap items-start gap-3">
                    <input
                      type="color"
                      class="h-11 w-14 cursor-pointer rounded-md border border-line bg-card p-1"
                      [value]="picker(spec.field)"
                      [attr.aria-label]="'Sélecteur de couleur : ' + spec.label"
                      (input)="pick(spec.field, $event)"
                    />
                    <div class="min-w-36 flex-1">
                      <label class="sr-only" [for]="'theme-' + spec.field">
                        Code hexadécimal : {{ spec.label }}
                      </label>
                      <input
                        type="text"
                        class="field font-mono uppercase"
                        autocomplete="off"
                        spellcheck="false"
                        maxlength="7"
                        [id]="'theme-' + spec.field"
                        [formControlName]="spec.field"
                        [attr.aria-describedby]="
                          describe(form.controls[spec.field], 'theme-' + spec.field + '-error')
                        "
                      />
                      <app-field-error
                        [control]="form.controls[spec.field]"
                        [errorId]="'theme-' + spec.field + '-error'"
                      />
                    </div>
                  </div>
                </fieldset>
              }

              @if (warnings().length > 0) {
                <div
                  class="rounded-md border border-line bg-primary-soft p-3 text-sm"
                  role="status"
                  aria-label="Avertissements"
                >
                  <p class="font-medium">Avertissement (vous pouvez tout de même enregistrer)</p>
                  <ul class="mt-1 list-disc pl-5">
                    @for (warning of warnings(); track warning) {
                      <li>{{ warning }}</li>
                    }
                  </ul>
                </div>
              }

              <p class="text-sm text-ink-muted">
                Contraste entre l’accent et le fond : {{ ratioLabel() }}.
              </p>

              @if (formError(); as message) {
                <p
                  class="rounded-md border border-line bg-primary-soft p-3 text-sm font-medium"
                  role="alert"
                >
                  {{ message }}
                </p>
              }
              @if (savedMessage(); as message) {
                <p
                  class="rounded-md border border-line bg-primary-soft p-3 text-sm font-medium"
                  role="status"
                >
                  {{ message }}
                </p>
              }

              <div class="flex flex-wrap items-center gap-3">
                <button type="submit" class="btn btn-accent" [disabled]="busy()">
                  {{ busy() ? 'Enregistrement…' : 'Enregistrer le thème' }}
                </button>
                @if (confirmReset()) {
                  <span class="text-sm">Rétablir la palette par défaut ?</span>
                  <button
                    type="button"
                    class="btn btn-secondary"
                    [disabled]="busy()"
                    (click)="reset()"
                  >
                    Confirmer la réinitialisation
                  </button>
                  <button type="button" class="btn btn-secondary" (click)="confirmReset.set(false)">
                    Annuler
                  </button>
                } @else {
                  <button
                    type="button"
                    class="btn btn-secondary"
                    [disabled]="busy()"
                    (click)="confirmReset.set(true)"
                  >
                    Réinitialiser
                  </button>
                }
              </div>
            </form>

            <div class="space-y-2">
              <h3 class="text-sm font-medium" id="theme-preview-title">Aperçu en direct</h3>
              <div
                class="theme-scope overflow-hidden rounded-lg border border-line"
                role="group"
                aria-labelledby="theme-preview-title"
                aria-describedby="theme-preview-help"
                [style]="previewVariables()"
              >
                <div aria-hidden="true">
                  <div class="flex items-center gap-3 bg-primary px-4 py-3 text-(--on-primary)">
                    <span class="font-semibold">DeskFlow</span>
                    <span class="text-sm">Tickets</span>
                  </div>
                  <ul class="space-y-2 p-4">
                    <li class="flex items-center gap-2 rounded-md border border-line bg-card p-3">
                      <span class="flex-1 text-sm font-medium">Impossible de se connecter</span>
                      <span class="badge-accent rounded-full px-2.5 py-0.5 text-xs font-medium">
                        Urgente
                      </span>
                    </li>
                    <li class="flex items-center gap-2 rounded-md border border-line bg-card p-3">
                      <span class="flex-1 text-sm font-medium">Écran qui scintille</span>
                      <span class="badge">Ouvert</span>
                    </li>
                    <li class="flex items-center gap-2 rounded-md border border-line bg-card p-3">
                      <span class="flex-1 text-sm font-medium">Mot de passe oublié</span>
                      <span class="badge badge-muted">Résolu</span>
                    </li>
                  </ul>
                  <div class="px-4 pb-4">
                    <span class="btn btn-accent">Nouveau ticket</span>
                  </div>
                </div>
              </div>
              <p id="theme-preview-help" class="text-sm text-ink-muted">
                Aperçu visuel : une barre de navigation, trois tickets d’exemple et un bouton
                d’action avec vos couleurs. Rien ne change dans l’application tant que vous n’avez
                pas enregistré.
              </p>
            </div>
          </div>
        }
      }
    </section>
  `,
})
export class ThemeSettings implements OnInit {
  private readonly api = inject(ApiClient);
  private readonly themeService = inject(ThemeService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly colors = COLORS;

  protected readonly form = inject(NonNullableFormBuilder).group({
    colorPrimary: ['', [Validators.required, hexValidator]],
    colorSurface: ['', [Validators.required, hexValidator]],
    colorAccent: ['', [Validators.required, hexValidator]],
  });

  protected readonly status = signal<'loading' | 'ready' | 'error'>('loading');
  protected readonly loadError = signal('');
  protected readonly busy = signal(false);
  protected readonly confirmReset = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly savedMessage = signal<string | null>(null);
  /** Warnings returned by the API for the saved theme; dropped as soon as a colour changes. */
  private readonly serverWarnings = signal<string[]>([]);

  /** Colours of the preview: the last valid value of each field, so typing never breaks it. */
  protected readonly previewTheme = signal<Theme | null>(null);

  /** CSS variables written on the preview container only, never on :root. */
  protected readonly previewVariables = computed(() => {
    const theme = this.previewTheme();
    return theme ? this.themeService.variablesFor(theme) : {};
  });

  protected readonly warnings = computed(() => {
    const theme = this.previewTheme();
    // Warnings of the API (after a save) and local ones are merged, without duplicates.
    const warnings = new Set<string>(this.serverWarnings());
    if (!theme) return [...warnings];
    if (accentContrastWarning(theme.colorAccent, theme.colorSurface)) {
      warnings.add(ACCENT_CONTRAST_WARNING);
    }
    const ink = this.themeService.inkContrast(theme.colorSurface);
    if (ink !== null && ink < MIN_TEXT_CONTRAST) {
      warnings.add(
        `Le texte principal n’atteint que ${formatRatio(ink)} de contraste sur cette couleur de fond (4,5:1 recommandé) : choisissez un fond plus clair.`,
      );
    }
    // Safety net: the text on primary and accent is dark, white or black, whichever reads best.
    for (const [label, color] of [
      ['principale', theme.colorPrimary],
      ['d’accent', theme.colorAccent],
    ] as const) {
      const ratio = this.themeService.textContrast(color);
      if (ratio < MIN_TEXT_CONTRAST) {
        warnings.add(
          `Le texte affiché sur la couleur ${label} n’atteint que ${formatRatio(ratio)} de contraste (4,5:1 recommandé) : choisissez une couleur plus claire ou plus foncée.`,
        );
      }
    }
    return [...warnings];
  });

  protected readonly ratioLabel = computed(() => {
    const theme = this.previewTheme();
    if (!theme) return '';
    return formatRatio(contrastRatio(theme.colorAccent, theme.colorSurface));
  });

  constructor() {
    // Editing invalidates what the server said about the previous save.
    this.form.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => {
      this.serverWarnings.set([]);
      this.savedMessage.set(null);
      this.previewTheme.update((last) => last && this.validColors(last));
    });
  }

  ngOnInit(): void {
    void this.load();
  }

  protected describe(control: AbstractControl, errorId: string): string | null {
    return describedBy(control, errorId);
  }

  /** Value of the native picker: always a valid colour (the preview one). */
  protected picker(field: ThemeField): string {
    return (this.previewTheme()?.[field] ?? '').toLowerCase();
  }

  protected pick(field: ThemeField, event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.form.controls[field].setValue(normalizeHex(value));
    this.form.controls[field].markAsDirty();
  }

  async load(): Promise<void> {
    this.status.set('loading');
    try {
      this.show(await firstValueFrom(this.api.getTheme()));
      this.status.set('ready');
    } catch (err) {
      this.loadError.set(errorMessage(err));
      this.status.set('error');
    }
  }

  protected async save(): Promise<void> {
    this.formError.set(null);
    this.savedMessage.set(null);
    this.confirmReset.set(false);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const value = this.form.getRawValue();
    const body: Theme = {
      colorPrimary: normalizeHex(value.colorPrimary),
      colorSurface: normalizeHex(value.colorSurface),
      colorAccent: normalizeHex(value.colorAccent),
    };
    this.busy.set(true);
    try {
      const result = await firstValueFrom(this.api.updateTheme(body));
      this.themeService.apply(result.theme);
      this.show(result.theme);
      this.serverWarnings.set(result.warnings);
      this.savedMessage.set('Thème enregistré : il s’applique maintenant à toute l’application.');
    } catch (err) {
      if (!(err instanceof ApiError && err.status === 422 && applyFieldErrors(this.form, err))) {
        this.formError.set(errorMessage(err));
      }
    } finally {
      this.busy.set(false);
    }
  }

  protected async reset(): Promise<void> {
    this.formError.set(null);
    this.savedMessage.set(null);
    this.busy.set(true);
    try {
      const theme = await firstValueFrom(this.api.resetTheme());
      this.themeService.apply(theme);
      this.show(theme);
      this.savedMessage.set('Palette par défaut rétablie.');
    } catch (err) {
      this.formError.set(errorMessage(err));
    } finally {
      this.confirmReset.set(false);
      this.busy.set(false);
    }
  }

  /** Puts a theme in the form (and the preview) without touching the application. */
  private show(theme: Theme): void {
    this.form.reset({
      colorPrimary: normalizeHex(theme.colorPrimary),
      colorSurface: normalizeHex(theme.colorSurface),
      colorAccent: normalizeHex(theme.colorAccent),
    });
    this.previewTheme.set({ ...theme });
    this.serverWarnings.set([]);
  }

  private validColors(previous: Theme): Theme {
    const raw = this.form.getRawValue();
    const pick = (field: ThemeField) =>
      isHexColor(raw[field].trim()) ? normalizeHex(raw[field]) : previous[field];
    return {
      colorPrimary: pick('colorPrimary'),
      colorSurface: pick('colorSurface'),
      colorAccent: pick('colorAccent'),
    };
  }
}
