import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import {
  PRIORITIES,
  PRIORITY_LABELS,
  SORT_LABELS,
  STATUSES,
  STATUS_LABELS,
} from '../../core/api/labels';
import { Category, TicketSort, UserRef } from '../../core/api/models';
import { isSlaBreached } from '../../core/api/ticket-rules';
import { AuthService } from '../../core/auth/auth.service';
import { Pager } from '../../shared/pager';
import { StatusMessage } from '../../shared/status-message';
import { PriorityBadge, SlaBadge, StatusBadge } from './ticket-badges';
import { TICKET_PAGE_SIZE, TicketFilters, TicketListState } from './ticket-list-state';

@Component({
  selector: 'app-ticket-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, RouterLink, Pager, StatusMessage, StatusBadge, PriorityBadge, SlaBadge],
  template: `
    <section class="space-y-6" aria-labelledby="tickets-title">
      <div class="flex flex-wrap items-center justify-between gap-3">
        <h1 id="tickets-title" class="text-2xl font-semibold">Tickets</h1>
        <a routerLink="/tickets/new" class="btn btn-accent">Nouveau ticket</a>
      </div>

      <form
        class="card grid gap-4 sm:grid-cols-2 lg:grid-cols-4"
        role="search"
        aria-label="Filtrer les tickets"
        (submit)="$event.preventDefault(); search(searchInput.value)"
      >
        <div class="sm:col-span-2 lg:col-span-4">
          <label for="filter-q" class="mb-1 block text-sm font-medium">
            Rechercher (titre ou numéro)
          </label>
          <div class="flex gap-2">
            <input
              #searchInput
              id="filter-q"
              type="search"
              class="field"
              maxlength="100"
              autocomplete="off"
              [value]="state.filters().q"
            />
            <button type="submit" class="btn btn-secondary">Rechercher</button>
          </div>
        </div>

        <div>
          <label for="filter-status" class="mb-1 block text-sm font-medium">Statut</label>
          <select
            id="filter-status"
            class="field"
            (change)="setFilter('status', $any($event.target).value)"
          >
            <option value="" [selected]="state.filters().status === ''">Tous les statuts</option>
            @for (status of statuses; track status) {
              <option [value]="status" [selected]="state.filters().status === status">
                {{ statusLabels[status] }}
              </option>
            }
          </select>
        </div>

        <div>
          <label for="filter-priority" class="mb-1 block text-sm font-medium">Priorité</label>
          <select
            id="filter-priority"
            class="field"
            (change)="setFilter('priority', $any($event.target).value)"
          >
            <option value="" [selected]="state.filters().priority === ''">
              Toutes les priorités
            </option>
            @for (priority of priorities; track priority) {
              <option [value]="priority" [selected]="state.filters().priority === priority">
                {{ priorityLabels[priority] }}
              </option>
            }
          </select>
        </div>

        @if (state.canFilterAssignee()) {
          <div>
            <label for="filter-assignee" class="mb-1 block text-sm font-medium">Assigné à</label>
            <select
              id="filter-assignee"
              class="field"
              (change)="setFilter('assignee', $any($event.target).value)"
            >
              <option value="" [selected]="state.filters().assignee === ''">Tout le monde</option>
              <option value="me" [selected]="state.filters().assignee === 'me'">Moi</option>
              <option value="unassigned" [selected]="state.filters().assignee === 'unassigned'">
                Non assigné
              </option>
              @for (assignee of assignees(); track assignee.id) {
                <option [value]="assignee.id" [selected]="state.filters().assignee === assignee.id">
                  {{ assignee.displayName }}
                </option>
              }
            </select>
          </div>
        }

        <div>
          <label for="filter-category" class="mb-1 block text-sm font-medium">Catégorie</label>
          <select
            id="filter-category"
            class="field"
            (change)="setFilter('categoryId', $any($event.target).value)"
          >
            <option value="" [selected]="state.filters().categoryId === ''">
              Toutes les catégories
            </option>
            @for (category of categories(); track category.id) {
              <option [value]="category.id" [selected]="state.filters().categoryId === category.id">
                {{ category.name }}
              </option>
            }
          </select>
        </div>

        <div>
          <label for="filter-sort" class="mb-1 block text-sm font-medium">Trier par</label>
          <select
            id="filter-sort"
            class="field"
            (change)="setFilter('sort', $any($event.target).value)"
          >
            @for (sort of sorts; track sort) {
              <option [value]="sort" [selected]="state.filters().sort === sort">
                {{ sortLabels[sort] }}
              </option>
            }
          </select>
        </div>

        <div>
          <label for="filter-direction" class="mb-1 block text-sm font-medium">Ordre</label>
          <select
            id="filter-direction"
            class="field"
            (change)="setFilter('direction', $any($event.target).value)"
          >
            <option value="desc" [selected]="state.filters().direction === 'desc'">
              Décroissant
            </option>
            <option value="asc" [selected]="state.filters().direction === 'asc'">Croissant</option>
          </select>
        </div>

        @if (state.hasActiveFilters()) {
          <div class="flex items-end">
            <button type="button" class="btn btn-secondary" (click)="reset(searchInput)">
              Réinitialiser les filtres
            </button>
          </div>
        }
      </form>

      @switch (state.status()) {
        @case ('loading') {
          <app-status-message kind="loading" message="Chargement des tickets…" />
        }
        @case ('error') {
          <app-status-message kind="error" [message]="state.error()" (retry)="state.load()" />
        }
        @default {
          @if (state.items().length === 0) {
            <app-status-message
              kind="empty"
              [message]="
                state.hasActiveFilters()
                  ? 'Aucun ticket ne correspond à ces filtres.'
                  : 'Aucun ticket pour le moment.'
              "
            />
          } @else {
            <p class="text-sm text-ink-muted" role="status">
              {{ state.total() }} ticket{{ state.total() > 1 ? 's' : '' }}
            </p>
            <div
              class="card table-wrap !p-0"
              tabindex="0"
              role="region"
              aria-label="Tableau des tickets, défilement horizontal possible"
            >
              <table class="table">
                <caption class="sr-only">
                  Liste des tickets
                </caption>
                <thead>
                  <tr>
                    <th scope="col">N°</th>
                    <th scope="col">Titre</th>
                    <th scope="col">Statut</th>
                    <th scope="col">Priorité</th>
                    <th scope="col">{{ isAgent() ? 'Demandeur' : 'Catégorie' }}</th>
                    @if (isAgent()) {
                      <th scope="col">Assigné à</th>
                    }
                    <th scope="col">Mis à jour</th>
                  </tr>
                </thead>
                <tbody>
                  @for (ticket of state.items(); track ticket.id) {
                    <tr>
                      <td class="whitespace-nowrap">#{{ ticket.number }}</td>
                      <td>
                        <a [routerLink]="['/tickets', ticket.id]" class="font-medium">
                          {{ ticket.title }}
                        </a>
                        @if (ticket.category) {
                          <span class="block text-ink-muted">{{ ticket.category.name }}</span>
                        }
                        @if (breached(ticket)) {
                          <app-sla-badge />
                        }
                      </td>
                      <td><app-status-badge [status]="ticket.status" /></td>
                      <td><app-priority-badge [priority]="ticket.priority" /></td>
                      <td>
                        @if (isAgent()) {
                          {{ ticket.requester.displayName }}
                        } @else {
                          {{ ticket.category?.name ?? '—' }}
                        }
                      </td>
                      @if (isAgent()) {
                        <td>{{ ticket.assignee?.displayName ?? 'Non assigné' }}</td>
                      }
                      <td class="whitespace-nowrap">
                        {{ ticket.updatedAt | date: 'dd/MM/yyyy HH:mm' }}
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            <app-pager
              [page]="state.page()"
              [pageSize]="pageSize"
              [total]="state.total()"
              (pageChange)="state.goToPage($event)"
            />
          }
        }
      }
    </section>
  `,
})
export class TicketList implements OnInit {
  private readonly api = inject(ApiClient);
  private readonly auth = inject(AuthService);
  protected readonly state = inject(TicketListState);

  protected readonly statuses = STATUSES;
  protected readonly statusLabels = STATUS_LABELS;
  protected readonly priorities = PRIORITIES;
  protected readonly priorityLabels = PRIORITY_LABELS;
  protected readonly sorts: TicketSort[] = ['createdAt', 'updatedAt', 'priority', 'number'];
  protected readonly sortLabels = SORT_LABELS;
  protected readonly pageSize = TICKET_PAGE_SIZE;

  protected readonly categories = signal<Category[]>([]);
  protected readonly assignees = signal<UserRef[]>([]);
  protected readonly isAgent = this.state.canFilterAssignee;

  ngOnInit(): void {
    void this.state.load();
    void this.loadOptions();
  }

  protected breached = isSlaBreached;

  protected setFilter<K extends keyof TicketFilters>(key: K, value: string): void {
    void this.state.setFilters({ [key]: value as TicketFilters[K] });
  }

  protected search(value: string): void {
    void this.state.setFilters({ q: value });
  }

  protected reset(search: HTMLInputElement): void {
    search.value = '';
    void this.state.reset();
  }

  /** Filter choices are best effort: a failure leaves the lists empty, the tickets still load. */
  private async loadOptions(): Promise<void> {
    try {
      this.categories.set(await firstValueFrom(this.api.listCategories()));
    } catch {
      this.categories.set([]);
    }
    if (this.state.canFilterAssignee() && this.auth.user()) {
      try {
        this.assignees.set(await firstValueFrom(this.api.listAssignees()));
      } catch {
        this.assignees.set([]);
      }
    }
  }
}
