import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  inject,
  signal,
} from '@angular/core';
import {
  AbstractControl,
  FormArray,
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  ValidationErrors,
} from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError, errorMessage } from '../../core/api/api-error';
import { PRIORITY_LABELS } from '../../core/api/labels';
import { SlaPolicy, TicketPriority } from '../../core/api/models';
import { FieldError, describedBy } from '../../shared/field-error';
import { StatusMessage } from '../../shared/status-message';
import {
  SLA_MAX,
  SLA_MIN,
  SLA_PRIORITIES,
  formatMinutes,
  mapSlaServerErrors,
  minutesError,
  resolutionError,
} from './sla-rules';

type Row = FormGroup<{
  first: FormControl<number | null>;
  resolution: FormControl<number | null>;
}>;

function minutesValidator(control: AbstractControl): ValidationErrors | null {
  const message = minutesError(control.value as number | null);
  return message ? { custom: { message } } : null;
}

function resolutionValidator(control: AbstractControl): ValidationErrors | null {
  const first = control.parent?.get('first')?.value as number | null | undefined;
  const message = resolutionError(first, control.value as number | null);
  return message ? { custom: { message } } : null;
}

/** SLA tab (US-20): one editable row per priority, in minutes, with readable durations. */
@Component({
  selector: 'app-sla-settings',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, FieldError, StatusMessage],
  template: `
    <section class="card space-y-4" aria-labelledby="sla-title">
      <h2 id="sla-title" class="text-xl font-semibold">Délais de service (SLA)</h2>
      <p class="text-sm text-ink-muted">
        Durées en minutes, de {{ min }} à {{ max }} (30 jours). Les nouveaux délais ne s’appliquent
        qu’aux nouveaux tickets : les échéances déjà posées ne sont pas recalculées.
      </p>

      @switch (status()) {
        @case ('loading') {
          <app-status-message kind="loading" message="Chargement des délais…" />
        }
        @case ('error') {
          <app-status-message kind="error" [message]="loadError()" (retry)="load()" />
        }
        @default {
          <form [formGroup]="form" (ngSubmit)="save()" novalidate class="space-y-4">
            <div class="table-wrap">
              <table class="table" formArrayName="rows">
                <caption class="sr-only">
                  Délais de première réponse et de résolution par priorité
                </caption>
                <thead>
                  <tr>
                    <th scope="col">Priorité</th>
                    <th scope="col">Première réponse (minutes)</th>
                    <th scope="col">Résolution (minutes)</th>
                  </tr>
                </thead>
                <tbody>
                  @for (row of rows.controls; track $index; let i = $index) {
                    <tr [formGroupName]="i">
                      <th scope="row" class="font-medium">{{ label(i) }}</th>
                      <td class="align-top">
                        <label class="sr-only" [for]="'sla-first-' + i">
                          Première réponse, priorité {{ label(i) }}, en minutes
                        </label>
                        <input
                          type="number"
                          inputmode="numeric"
                          class="field max-w-40"
                          [id]="'sla-first-' + i"
                          formControlName="first"
                          [attr.aria-describedby]="
                            describe(
                              row.controls.first,
                              'sla-first-' + i + '-error',
                              'sla-first-' + i + '-hint'
                            )
                          "
                        />
                        <p class="mt-1 text-sm text-ink-muted" [id]="'sla-first-' + i + '-hint'">
                          {{ readable(row.controls.first.value) }}
                        </p>
                        <app-field-error
                          [control]="row.controls.first"
                          [errorId]="'sla-first-' + i + '-error'"
                        />
                      </td>
                      <td class="align-top">
                        <label class="sr-only" [for]="'sla-resolution-' + i">
                          Résolution, priorité {{ label(i) }}, en minutes
                        </label>
                        <input
                          type="number"
                          inputmode="numeric"
                          class="field max-w-40"
                          [id]="'sla-resolution-' + i"
                          formControlName="resolution"
                          [attr.aria-describedby]="
                            describe(
                              row.controls.resolution,
                              'sla-resolution-' + i + '-error',
                              'sla-resolution-' + i + '-hint'
                            )
                          "
                        />
                        <p
                          class="mt-1 text-sm text-ink-muted"
                          [id]="'sla-resolution-' + i + '-hint'"
                        >
                          {{ readable(row.controls.resolution.value) }}
                        </p>
                        <app-field-error
                          [control]="row.controls.resolution"
                          [errorId]="'sla-resolution-' + i + '-error'"
                        />
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>

            @if (formError().length > 0) {
              <div
                class="rounded-md border border-line bg-primary-soft p-3 text-sm font-medium"
                role="alert"
              >
                @for (message of formError(); track message) {
                  <p>{{ message }}</p>
                }
              </div>
            }
            @if (saved()) {
              <p
                class="rounded-md border border-line bg-primary-soft p-3 text-sm font-medium"
                role="status"
              >
                Délais enregistrés. Ils s’appliquent aux tickets créés à partir de maintenant.
              </p>
            }

            <button type="submit" class="btn btn-accent" [disabled]="busy()">
              {{ busy() ? 'Enregistrement…' : 'Enregistrer les délais' }}
            </button>
          </form>
        }
      }
    </section>
  `,
})
export class SlaSettings implements OnInit {
  private readonly api = inject(ApiClient);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly min = SLA_MIN;
  protected readonly max = SLA_MAX;

  protected readonly rows = new FormArray<Row>([]);
  protected readonly form = new FormGroup({ rows: this.rows });

  constructor() {
    // An edit makes the "saved" confirmation obsolete.
    this.form.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.saved.set(false));
  }

  protected readonly status = signal<'loading' | 'ready' | 'error'>('loading');
  protected readonly loadError = signal('');
  protected readonly busy = signal(false);
  protected readonly saved = signal(false);
  protected readonly formError = signal<string[]>([]);

  private priorities: readonly TicketPriority[] = SLA_PRIORITIES;

  ngOnInit(): void {
    void this.load();
  }

  protected label(index: number): string {
    return PRIORITY_LABELS[this.priorities[index]];
  }

  protected readable(minutes: number | null): string {
    return formatMinutes(minutes) ? `= ${formatMinutes(minutes)}` : '';
  }

  protected describe(control: AbstractControl, errorId: string, hintId: string): string | null {
    return describedBy(control, errorId, hintId);
  }

  async load(): Promise<void> {
    this.status.set('loading');
    try {
      this.setRows(await firstValueFrom(this.api.listSlaPolicies()));
      this.status.set('ready');
    } catch (err) {
      this.loadError.set(errorMessage(err));
      this.status.set('error');
    }
  }

  protected async save(): Promise<void> {
    this.formError.set([]);
    this.saved.set(false);
    this.form.markAllAsTouched();
    this.rows.controls.forEach((row) => row.controls.resolution.updateValueAndValidity());
    if (this.form.invalid) return;

    const policies: SlaPolicy[] = this.rows.controls.map((row, i) => ({
      priority: this.priorities[i],
      firstResponseMinutes: Number(row.controls.first.value),
      resolutionMinutes: Number(row.controls.resolution.value),
    }));
    this.busy.set(true);
    try {
      this.setRows(await firstValueFrom(this.api.updateSlaPolicies({ policies })));
      this.saved.set(true);
    } catch (err) {
      this.showServerError(err);
    } finally {
      this.busy.set(false);
    }
  }

  private showServerError(err: unknown): void {
    if (!(err instanceof ApiError) || err.status !== 422) {
      this.formError.set([errorMessage(err)]);
      return;
    }
    const { fields, general } = mapSlaServerErrors(err.fieldErrors);
    this.rows.controls.forEach((row, i) => {
      for (const name of ['first', 'resolution'] as const) {
        const message = fields.get(`${i}:${name}`);
        if (!message) continue;
        row.controls[name].setErrors({ custom: { message } });
        row.controls[name].markAsTouched();
      }
    });
    // Without a message for a cell, the general alert tells the user the save was refused.
    this.formError.set(general.length > 0 || fields.size > 0 ? general : [err.message]);
  }

  /** Rebuilds the four rows, in the API order, from the server state. */
  private setRows(policies: SlaPolicy[]): void {
    this.rows.clear({ emitEvent: false });
    this.priorities = policies.map((p) => p.priority);
    for (const policy of policies) {
      const row: Row = new FormGroup({
        first: new FormControl<number | null>(policy.firstResponseMinutes, minutesValidator),
        resolution: new FormControl<number | null>(policy.resolutionMinutes, resolutionValidator),
      });
      // A shorter first response can make the resolution valid again, and the reverse.
      row.controls.first.valueChanges
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe(() => row.controls.resolution.updateValueAndValidity());
      this.rows.push(row);
    }
  }
}
