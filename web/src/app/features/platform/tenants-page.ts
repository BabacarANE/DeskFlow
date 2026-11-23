import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError, errorMessage } from '../../core/api/api-error';
import { Tenant } from '../../core/api/models';
import { FieldError, applyFieldErrors } from '../../shared/field-error';
import { Pager } from '../../shared/pager';
import { StatusMessage } from '../../shared/status-message';

const PAGE_SIZE = 20;
const SLUG_PATTERN = /^[a-z0-9]+(-[a-z0-9]+)*$/;

/** SuperAdmin: companies list, creation (with the first admin invited) and suspension. */
@Component({
  selector: 'app-tenants-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, ReactiveFormsModule, FieldError, Pager, StatusMessage],
  template: `
    <div class="space-y-10">
      <h1 class="text-2xl font-semibold">Entreprises</h1>

      <section class="card space-y-4" aria-labelledby="create-title">
        <h2 id="create-title" class="text-xl font-semibold">Créer une entreprise</h2>
        <p class="text-sm text-ink-muted">
          Le premier administrateur reçoit une invitation par e-mail.
        </p>
        <form class="grid gap-4 sm:grid-cols-2" [formGroup]="form" (ngSubmit)="submit()" novalidate>
          <div>
            <label for="tenant-name" class="mb-1 block text-sm font-medium">Nom</label>
            <input id="tenant-name" type="text" class="field" formControlName="name" />
            <app-field-error [control]="form.controls.name" />
          </div>
          <div>
            <label for="tenant-slug" class="mb-1 block text-sm font-medium">
              Identifiant (slug)
            </label>
            <input
              id="tenant-slug"
              type="text"
              class="field"
              autocapitalize="none"
              formControlName="slug"
            />
            <p class="mt-1 text-xs text-ink-muted">
              Minuscules, chiffres et tirets, par exemple « acme-sas ».
            </p>
            <app-field-error [control]="form.controls.slug" />
          </div>
          <div class="sm:col-span-2">
            <label for="tenant-admin" class="mb-1 block text-sm font-medium">
              E-mail du premier administrateur
            </label>
            <input
              id="tenant-admin"
              type="email"
              class="field"
              autocomplete="off"
              formControlName="adminEmail"
            />
            <app-field-error [control]="form.controls.adminEmail" />
          </div>
          <div class="sm:col-span-2">
            <button type="submit" class="btn btn-accent" [disabled]="busy()">
              Créer l’entreprise
            </button>
          </div>
        </form>
        @if (formError(); as message) {
          <p
            class="rounded-md border border-line bg-primary-soft p-3 text-sm font-medium"
            role="alert"
          >
            {{ message }}
          </p>
        }
        @if (success(); as message) {
          <p
            class="rounded-md border border-line bg-primary-soft p-3 text-sm font-medium"
            role="status"
          >
            {{ message }}
          </p>
        }
      </section>

      <section class="card space-y-4" aria-labelledby="tenants-title">
        <h2 id="tenants-title" class="text-xl font-semibold">Liste des entreprises</h2>

        @if (actionError(); as message) {
          <p
            class="rounded-md border border-line bg-primary-soft p-3 text-sm font-medium"
            role="alert"
          >
            {{ message }}
          </p>
        }

        @switch (status()) {
          @case ('loading') {
            <app-status-message kind="loading" message="Chargement des entreprises…" />
          }
          @case ('error') {
            <app-status-message kind="error" [message]="loadError()" (retry)="load()" />
          }
          @default {
            @if (tenants().length === 0) {
              <app-status-message kind="empty" message="Aucune entreprise pour le moment." />
            } @else {
              <div class="table-wrap">
                <table class="table">
                  <caption class="sr-only">
                    Entreprises de la plateforme
                  </caption>
                  <thead>
                    <tr>
                      <th scope="col">Nom</th>
                      <th scope="col">Identifiant</th>
                      <th scope="col">Statut</th>
                      <th scope="col">Créée le</th>
                      <th scope="col"><span class="sr-only">Actions</span></th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (tenant of tenants(); track tenant.id) {
                      <tr>
                        <td class="font-medium">{{ tenant.name }}</td>
                        <td>{{ tenant.slug }}</td>
                        <td>
                          <span class="badge" [class.badge-muted]="!tenant.isActive">
                            {{ tenant.isActive ? 'Active' : 'Suspendue' }}
                          </span>
                        </td>
                        <td>{{ tenant.createdAt | date: 'dd/MM/yyyy' }}</td>
                        <td class="text-right">
                          @if (confirmId() === tenant.id) {
                            <button
                              type="button"
                              class="btn btn-secondary btn-small"
                              [disabled]="busyId() === tenant.id"
                              (click)="toggle(tenant)"
                            >
                              Confirmer
                              <span class="sr-only">pour {{ tenant.name }}</span>
                            </button>
                            <button
                              type="button"
                              class="btn btn-secondary btn-small ml-1"
                              (click)="confirmId.set(null)"
                            >
                              Annuler
                            </button>
                          } @else {
                            <button
                              type="button"
                              class="btn btn-secondary btn-small"
                              (click)="confirmId.set(tenant.id)"
                            >
                              {{ tenant.isActive ? 'Suspendre' : 'Réactiver' }}
                              <span class="sr-only">{{ tenant.name }}</span>
                            </button>
                          }
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
              <app-pager
                [page]="page()"
                [pageSize]="pageSize"
                [total]="total()"
                (pageChange)="load($event)"
              />
            }
          }
        }
      </section>
    </div>
  `,
})
export class TenantsPage implements OnInit {
  private readonly api = inject(ApiClient);

  protected readonly pageSize = PAGE_SIZE;
  protected readonly form = inject(NonNullableFormBuilder).group({
    name: ['', [Validators.required, Validators.maxLength(100)]],
    slug: ['', [Validators.required, Validators.maxLength(50), Validators.pattern(SLUG_PATTERN)]],
    adminEmail: ['', [Validators.required, Validators.email]],
  });

  protected readonly status = signal<'loading' | 'ready' | 'error'>('loading');
  protected readonly loadError = signal('');
  protected readonly formError = signal<string | null>(null);
  protected readonly success = signal<string | null>(null);
  protected readonly actionError = signal<string | null>(null);
  protected readonly tenants = signal<Tenant[]>([]);
  protected readonly total = signal(0);
  protected readonly page = signal(1);
  protected readonly busy = signal(false);
  protected readonly busyId = signal<string | null>(null);
  protected readonly confirmId = signal<string | null>(null);

  ngOnInit(): void {
    void this.load();
  }

  async load(page = this.page()): Promise<void> {
    this.status.set('loading');
    try {
      const result = await firstValueFrom(this.api.listTenants(page, PAGE_SIZE));
      this.tenants.set(result.items);
      this.total.set(result.total);
      this.page.set(result.page);
      this.status.set('ready');
    } catch (err) {
      this.loadError.set(errorMessage(err));
      this.status.set('error');
    }
  }

  protected async submit(): Promise<void> {
    this.formError.set(null);
    this.success.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.busy.set(true);
    try {
      const request = this.form.getRawValue();
      const created = await firstValueFrom(this.api.createTenant(request));
      this.success.set(
        `Entreprise « ${created.name} » créée. Une invitation a été envoyée à ${request.adminEmail}.`,
      );
      this.form.reset();
      await this.load(1);
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        this.formError.set('Une entreprise utilise déjà cet identifiant (slug).');
      } else if (!(err instanceof ApiError && applyFieldErrors(this.form, err))) {
        this.formError.set(errorMessage(err));
      }
    } finally {
      this.busy.set(false);
    }
  }

  protected async toggle(tenant: Tenant): Promise<void> {
    this.busyId.set(tenant.id);
    this.actionError.set(null);
    try {
      const updated = await firstValueFrom(
        this.api.updateTenant(tenant.id, { isActive: !tenant.isActive }),
      );
      this.tenants.update((list) => list.map((t) => (t.id === updated.id ? updated : t)));
    } catch (err) {
      this.actionError.set(errorMessage(err));
    } finally {
      this.busyId.set(null);
      this.confirmId.set(null);
    }
  }
}
