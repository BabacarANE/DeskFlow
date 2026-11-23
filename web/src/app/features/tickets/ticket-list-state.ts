import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { errorMessage } from '../../core/api/api-error';
import {
  SortDirection,
  TicketPriority,
  TicketQuery,
  TicketSort,
  TicketStatus,
  TicketSummary,
} from '../../core/api/models';
import { isAgentRole } from '../../core/api/ticket-rules';
import { AuthService } from '../../core/auth/auth.service';

export const TICKET_PAGE_SIZE = 20;

/** `me` and `unassigned` are shortcuts; anything else is a user id. */
export type AssigneeFilter = '' | 'me' | 'unassigned' | (string & {});

export interface TicketFilters {
  status: '' | TicketStatus;
  priority: '' | TicketPriority;
  assignee: AssigneeFilter;
  categoryId: string;
  q: string;
  sort: TicketSort;
  direction: SortDirection;
}

const DEFAULT_FILTERS: TicketFilters = {
  status: '',
  priority: '',
  assignee: '',
  categoryId: '',
  q: '',
  sort: 'createdAt',
  direction: 'desc',
};

/**
 * State of the ticket list: filters, sort, page and the loaded page.
 * Provided in root so that filters survive a visit to a ticket and back.
 */
@Injectable({ providedIn: 'root' })
export class TicketListState {
  private readonly api = inject(ApiClient);
  private readonly auth = inject(AuthService);
  private requestId = 0;

  readonly filters = signal<TicketFilters>({ ...DEFAULT_FILTERS });
  readonly page = signal(1);
  readonly items = signal<TicketSummary[]>([]);
  readonly total = signal(0);
  readonly status = signal<'loading' | 'ready' | 'error'>('loading');
  readonly error = signal('');

  /** A Requester only sees their own tickets: no assignee filter. */
  readonly canFilterAssignee = computed(() => isAgentRole(this.auth.role()));

  readonly hasActiveFilters = computed(() => {
    const f = this.filters();
    return (
      f.status !== '' ||
      f.priority !== '' ||
      (this.canFilterAssignee() && f.assignee !== '') ||
      f.categoryId !== '' ||
      f.q.trim() !== ''
    );
  });

  /** The query sent to GET /tickets (absent keys are omitted). */
  readonly query = computed<TicketQuery>(() => {
    const f = this.filters();
    const query: TicketQuery = {
      sort: f.sort,
      direction: f.direction,
      page: this.page(),
      pageSize: TICKET_PAGE_SIZE,
    };
    if (f.status) query.status = f.status;
    if (f.priority) query.priority = f.priority;
    if (f.categoryId) query.categoryId = f.categoryId;
    if (f.q.trim()) query.q = f.q.trim();
    if (this.canFilterAssignee()) {
      if (f.assignee === 'unassigned') query.unassigned = true;
      else if (f.assignee === 'me') {
        const id = this.auth.user()?.id;
        if (id) query.assigneeId = id;
      } else if (f.assignee) query.assigneeId = f.assignee;
    }
    return query;
  });

  /** Changes filters or sort and goes back to the first page. */
  async setFilters(patch: Partial<TicketFilters>): Promise<void> {
    this.filters.update((current) => ({ ...current, ...patch }));
    this.page.set(1);
    await this.load();
  }

  async reset(): Promise<void> {
    this.filters.set({ ...DEFAULT_FILTERS });
    this.page.set(1);
    await this.load();
  }

  async goToPage(page: number): Promise<void> {
    this.page.set(page);
    await this.load();
  }

  /**
   * Refetches the current query without the loading state, so filters, typed text and focus stay
   * untouched. Skipped while a regular load runs or after an error; failures are ignored.
   */
  async reloadQuietly(): Promise<void> {
    if (this.status() !== 'ready') return;
    const id = this.requestId;
    try {
      const result = await firstValueFrom(this.api.listTickets(this.query()));
      if (id !== this.requestId || this.status() !== 'ready') return;
      this.items.set(result.items);
      this.total.set(result.total);
    } catch {
      // The next event or an explicit action retries.
    }
  }

  async load(): Promise<void> {
    const id = ++this.requestId;
    this.status.set('loading');
    try {
      const result = await firstValueFrom(this.api.listTickets(this.query()));
      if (id !== this.requestId) return; // a newer request superseded this one
      this.items.set(result.items);
      this.total.set(result.total);
      this.page.set(result.page);
      this.status.set('ready');
    } catch (err) {
      if (id !== this.requestId) return;
      this.error.set(errorMessage(err));
      this.status.set('error');
    }
  }
}
