import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError, errorMessage } from '../../core/api/api-error';
import { ASSIGNABLE_ROLES, INVITATION_STATUS_LABELS, ROLE_LABELS } from '../../core/api/labels';
import { AssignableRole, Invitation } from '../../core/api/models';
import { FieldError, applyFieldErrors } from '../../shared/field-error';
import { Pager } from '../../shared/pager';
import { StatusMessage } from '../../shared/status-message';

const PAGE_SIZE = 20;

@Component({
  selector: 'app-invitations-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, ReactiveFormsModule, FieldError, Pager, StatusMessage],
  template: `
    <section class="card space-y-6" aria-labelledby="invitations-title">
      <h2 id="invitations-title" class="text-xl font-semibold">Invitations</h2>

      <form
        class="grid gap-4 sm:grid-cols-[1fr_auto_auto] sm:items-start"
        [formGroup]="form"
        (ngSubmit)="submit()"
        novalidate
      >
        <div>
          <label for="invitation-email" class="mb-1 block text-sm font-medium">
            Adresse e-mail
          </label>
          <input
            id="invitation-email"
            type="email"
            class="field"
            autocomplete="off"
            formControlName="email"
          />
          <app-field-error [control]="form.controls.email" />
        </div>
        <div>
          <label for="invitation-role" class="mb-1 block text-sm font-medium">Rôle</label>
          <select id="invitation-role" class="field" formControlName="role">
            @for (role of roles; track role) {
              <option [value]="role">{{ roleLabels[role] }}</option>
            }
          </select>
        </div>
        <button type="submit" class="btn btn-accent sm:mt-6" [disabled]="busy()">Inviter</button>
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

      @switch (status()) {
        @case ('loading') {
          <app-status-message kind="loading" message="Chargement des invitations…" />
        }
        @case ('error') {
          <app-status-message kind="error" [message]="loadError()" (retry)="load()" />
        }
        @default {
          @if (invitations().length === 0) {
            <app-status-message kind="empty" message="Aucune invitation pour le moment." />
          } @else {
            <div class="table-wrap">
              <table class="table">
                <caption class="sr-only">
                  Invitations de l’entreprise
                </caption>
                <thead>
                  <tr>
                    <th scope="col">Adresse e-mail</th>
                    <th scope="col">Rôle</th>
                    <th scope="col">Statut</th>
                    <th scope="col">Expire le</th>
                    <th scope="col"><span class="sr-only">Actions</span></th>
                  </tr>
                </thead>
                <tbody>
                  @for (invitation of invitations(); track invitation.id) {
                    <tr>
                      <td>{{ invitation.email }}</td>
                      <td>{{ roleLabels[invitation.role] }}</td>
                      <td>
                        <span class="badge" [class.badge-muted]="invitation.status !== 'Pending'">
                          {{ statusLabels[invitation.status] }}
                        </span>
                      </td>
                      <td>{{ invitation.expiresAt | date: 'dd/MM/yyyy' }}</td>
                      <td class="text-right">
                        @if (invitation.status === 'Pending') {
                          @if (confirmId() === invitation.id) {
                            <button
                              type="button"
                              class="btn btn-secondary btn-small"
                              [disabled]="busyId() === invitation.id"
                              (click)="revoke(invitation)"
                            >
                              Confirmer
                              <span class="sr-only">la révocation pour {{ invitation.email }}</span>
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
                              (click)="confirmId.set(invitation.id)"
                            >
                              Révoquer
                              <span class="sr-only">l’invitation de {{ invitation.email }}</span>
                            </button>
                          }
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
  `,
})
export class InvitationsList implements OnInit {
  private readonly api = inject(ApiClient);

  protected readonly roles = ASSIGNABLE_ROLES;
  protected readonly roleLabels = ROLE_LABELS;
  protected readonly statusLabels = INVITATION_STATUS_LABELS;
  protected readonly pageSize = PAGE_SIZE;

  protected readonly form = inject(NonNullableFormBuilder).group({
    email: ['', [Validators.required, Validators.email]],
    role: ['Requester' as AssignableRole, Validators.required],
  });

  protected readonly status = signal<'loading' | 'ready' | 'error'>('loading');
  protected readonly loadError = signal('');
  protected readonly formError = signal<string | null>(null);
  protected readonly success = signal<string | null>(null);
  protected readonly invitations = signal<Invitation[]>([]);
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
      const result = await firstValueFrom(this.api.listInvitations(page, PAGE_SIZE));
      this.invitations.set(result.items);
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
      const created = await firstValueFrom(this.api.createInvitation(this.form.getRawValue()));
      this.success.set(`Invitation envoyée à ${created.email}.`);
      this.form.controls.email.reset('');
      await this.load(1);
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        this.formError.set(
          'Une invitation en attente ou un compte existe déjà pour cette adresse.',
        );
      } else if (!(err instanceof ApiError && applyFieldErrors(this.form, err))) {
        this.formError.set(errorMessage(err));
      }
    } finally {
      this.busy.set(false);
    }
  }

  protected async revoke(invitation: Invitation): Promise<void> {
    this.busyId.set(invitation.id);
    this.formError.set(null);
    this.success.set(null);
    try {
      await firstValueFrom(this.api.revokeInvitation(invitation.id));
      this.confirmId.set(null);
      await this.load();
    } catch (err) {
      this.formError.set(errorMessage(err));
    } finally {
      this.busyId.set(null);
    }
  }
}
