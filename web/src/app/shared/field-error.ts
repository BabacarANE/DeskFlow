import { ChangeDetectionStrategy, Component, computed, effect, input, signal } from '@angular/core';
import { AbstractControl, FormGroup } from '@angular/forms';
import { ApiError } from '../core/api/api-error';

const MESSAGES: Record<string, (e: Record<string, unknown>) => string> = {
  required: () => 'Ce champ est obligatoire.',
  email: () => 'Saisissez une adresse e-mail valide.',
  maxlength: (e) => `Ce champ ne peut pas dépasser ${String(e['requiredLength'])} caractères.`,
  minlength: (e) => `Ce champ doit comporter au moins ${String(e['requiredLength'])} caractères.`,
  min: (e) => `La valeur minimale est ${String(e['min'])}.`,
  max: (e) => `La valeur maximale est ${String(e['max'])}.`,
  pattern: () => 'Le format est invalide.',
  custom: (e) => String(e['message']),
  server: (e) => String(e['message']),
};

/** Shows the first validation message of a control once it has been touched. */
@Component({
  selector: 'app-field-error',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (message(); as text) {
      <p class="mt-1 text-sm font-medium" role="alert" [attr.id]="errorId()">{{ text }}</p>
    }
  `,
})
export class FieldError {
  readonly control = input.required<AbstractControl>();
  /** Id of the message, referenced by the field through aria-describedby (see describedBy). */
  readonly errorId = input<string | null>(null);

  /** Bumped on every touched, status or value event of the control, so the OnPush view refreshes. */
  private readonly changes = signal(0);

  protected readonly message = computed(() => {
    this.changes();
    const control = this.control();
    if (!control.errors || !(control.touched || control.dirty)) return null;
    for (const key of Object.keys(control.errors)) {
      const format = MESSAGES[key];
      if (format) return format((control.errors[key] ?? {}) as Record<string, unknown>);
    }
    return 'Valeur invalide.';
  });

  constructor() {
    effect((onCleanup) => {
      const subscription = this.control().events.subscribe(() => this.changes.update((n) => n + 1));
      onCleanup(() => subscription.unsubscribe());
    });
  }
}

/** Copies the 422 `errors` map of a ProblemDetails onto matching form controls. */
export function applyFieldErrors(form: FormGroup, error: ApiError): boolean {
  let applied = false;
  for (const [field, messages] of Object.entries(error.fieldErrors)) {
    const name = Object.keys(form.controls).find((n) => n.toLowerCase() === field.toLowerCase());
    if (!name) continue;
    const control = form.controls[name];
    control.setErrors({ server: { message: messages.join(' ') } });
    control.markAsTouched();
    applied = true;
  }
  return applied;
}

/** True when a message is displayed for the control (same rule as FieldError). */
export function showsError(control: AbstractControl): boolean {
  return !!control.errors && (control.touched || control.dirty);
}

/**
 * Value of aria-describedby for a field: the error message (only while it is displayed) and any
 * permanent hint.
 */
export function describedBy(
  control: AbstractControl,
  errorId: string,
  ...hintIds: string[]
): string | null {
  const ids = [...hintIds, ...(showsError(control) ? [errorId] : [])];
  return ids.length > 0 ? ids.join(' ') : null;
}
