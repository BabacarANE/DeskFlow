import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { errorMessage } from '../../core/api/api-error';
import { ASSIGNABLE_ROLES, ROLE_LABELS } from '../../core/api/labels';
import { AssignableRole, Role, User } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { Pager } from '../../shared/pager';
import { StatusMessage } from '../../shared/status-message';

const PAGE_SIZE = 20;

@Component({
  selector: 'app-users-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, Pager, StatusMessage],
  template: `
    <section class="card space-y-4" aria-labelledby="users-title">
      <h2 id="users-title" class="text-xl font-semibold">Utilisateurs</h2>

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
          <app-status-message kind="loading" message="Chargement des utilisateurs…" />
        }
        @case ('error') {
          <app-status-message kind="error" [message]="loadError()" (retry)="load()" />
        }
        @default {
          @if (users().length === 0) {
            <app-status-message kind="empty" message="Aucun utilisateur pour le moment." />
          } @else {
            <div class="table-wrap">
              <table class="table">
                <caption class="sr-only">
                  Utilisateurs de l’entreprise
                </caption>
                <thead>
                  <tr>
                    <th scope="col">Nom</th>
                    <th scope="col">Rôle</th>
                    <th scope="col">Statut</th>
                    <th scope="col">Créé le</th>
                    <th scope="col"><span class="sr-only">Actions</span></th>
                  </tr>
                </thead>
                <tbody>
                  @for (user of users(); track user.id) {
                    <tr>
                      <td>
                        <span class="block font-medium">{{ user.displayName }}</span>
                        <span class="block text-ink-muted">{{ user.email }}</span>
                      </td>
                      <td>
                        @if (isSelf(user) || user.role === 'SuperAdmin') {
                          {{ roleLabels[user.role] }}
                          @if (isSelf(user)) {
                            <span class="text-ink-muted"> (vous)</span>
                          }
                        } @else {
                          <label class="sr-only" [for]="'role-' + user.id">
                            Rôle de {{ user.displayName }}
                          </label>
                          <select
                            class="field"
                            [id]="'role-' + user.id"
                            [disabled]="busyId() === user.id"
                            (change)="changeRole(user, $event)"
                          >
                            @for (role of assignableRoles; track role) {
                              <option [value]="role" [selected]="role === user.role">
                                {{ roleLabels[role] }}
                              </option>
                            }
                          </select>
                        }
                      </td>
                      <td>
                        <span class="badge" [class.badge-muted]="!user.isActive">
                          {{ user.isActive ? 'Actif' : 'Désactivé' }}
                        </span>
                      </td>
                      <td>{{ user.createdAt | date: 'dd/MM/yyyy' }}</td>
                      <td class="text-right">
                        @if (!isSelf(user) && user.role !== 'SuperAdmin') {
                          @if (confirmId() === user.id) {
                            <button
                              type="button"
                              class="btn btn-secondary btn-small"
                              [disabled]="busyId() === user.id"
                              (click)="toggleActive(user)"
                            >
                              Confirmer
                              <span class="sr-only">pour {{ user.displayName }}</span>
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
                              [disabled]="busyId() === user.id"
                              (click)="askConfirm(user)"
                            >
                              {{ user.isActive ? 'Désactiver' : 'Réactiver' }}
                              <span class="sr-only">{{ user.displayName }}</span>
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
export class UsersList implements OnInit {
  private readonly api = inject(ApiClient);
  private readonly auth = inject(AuthService);

  protected readonly roleLabels = ROLE_LABELS;
  protected readonly assignableRoles = ASSIGNABLE_ROLES;
  protected readonly pageSize = PAGE_SIZE;

  protected readonly status = signal<'loading' | 'ready' | 'error'>('loading');
  protected readonly loadError = signal('');
  protected readonly actionError = signal<string | null>(null);
  protected readonly users = signal<User[]>([]);
  protected readonly total = signal(0);
  protected readonly page = signal(1);
  protected readonly busyId = signal<string | null>(null);
  protected readonly confirmId = signal<string | null>(null);

  ngOnInit(): void {
    void this.load();
  }

  protected isSelf(user: User): boolean {
    return user.id === this.auth.user()?.id;
  }

  async load(page = this.page()): Promise<void> {
    this.status.set('loading');
    try {
      const result = await firstValueFrom(this.api.listUsers(page, PAGE_SIZE));
      this.users.set(result.items);
      this.total.set(result.total);
      this.page.set(result.page);
      this.status.set('ready');
    } catch (err) {
      this.loadError.set(errorMessage(err));
      this.status.set('error');
    }
  }

  protected askConfirm(user: User): void {
    this.confirmId.set(user.id);
  }

  protected async changeRole(user: User, event: Event): Promise<void> {
    const select = event.target as HTMLSelectElement;
    const role = select.value as Role;
    if (role === user.role || role === 'SuperAdmin') return;
    const ok = await this.update(user, { role });
    // Put the selector back in line with the server state when the change was refused.
    if (!ok) select.value = user.role;
  }

  protected async toggleActive(user: User): Promise<void> {
    await this.update(user, { isActive: !user.isActive });
  }

  private async update(
    user: User,
    patch: { role?: AssignableRole; isActive?: boolean },
  ): Promise<boolean> {
    this.busyId.set(user.id);
    this.actionError.set(null);
    try {
      const updated = await firstValueFrom(this.api.updateUser(user.id, patch));
      this.users.update((list) => list.map((u) => (u.id === updated.id ? updated : u)));
      return true;
    } catch (err) {
      this.actionError.set(errorMessage(err));
      return false;
    } finally {
      this.busyId.set(null);
      this.confirmId.set(null);
    }
  }
}
