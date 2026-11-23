import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError } from '../../core/api/api-error';
import { Page, Role, TicketSummary } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { summary } from '../../testing/ticket-fixtures';
import { TicketListState } from './ticket-list-state';

const page = (items: TicketSummary[], total = items.length, n = 1): Page<TicketSummary> => ({
  items,
  total,
  page: n,
  pageSize: 20,
});

describe('TicketListState', () => {
  const api = { listTickets: vi.fn() };
  let state: TicketListState;

  function setup(role: Role) {
    api.listTickets.mockReset();
    api.listTickets.mockReturnValue(of(page([summary('t1')])));
    TestBed.configureTestingModule({
      providers: [
        { provide: ApiClient, useValue: api },
        { provide: AuthService, useValue: { role: () => role, user: () => ({ id: 'u-me' }) } },
      ],
    });
    state = TestBed.inject(TicketListState);
  }

  const lastQuery = () => api.listTickets.mock.calls.at(-1)?.[0];

  it('loads the first page with the default sort', async () => {
    setup('Agent');
    await state.load();
    expect(lastQuery()).toEqual({
      sort: 'createdAt',
      direction: 'desc',
      page: 1,
      pageSize: 20,
    });
    expect(state.items()).toHaveLength(1);
    expect(state.status()).toBe('ready');
  });

  it('translates filters into the contract query and goes back to page 1', async () => {
    setup('Agent');
    await state.goToPage(3);
    await state.setFilters({ status: 'Open', priority: 'Urgent', categoryId: 'c1', q: ' vpn ' });
    expect(lastQuery()).toMatchObject({
      status: 'Open',
      priority: 'Urgent',
      categoryId: 'c1',
      q: 'vpn',
      page: 1,
    });
    expect(state.hasActiveFilters()).toBe(true);
  });

  it('maps the assignee shortcuts: me, unassigned and a user id', async () => {
    setup('Agent');
    await state.setFilters({ assignee: 'me' });
    expect(lastQuery()).toMatchObject({ assigneeId: 'u-me' });
    await state.setFilters({ assignee: 'unassigned' });
    expect(lastQuery().unassigned).toBe(true);
    expect(lastQuery().assigneeId).toBeUndefined();
    await state.setFilters({ assignee: 'u42' });
    expect(lastQuery()).toMatchObject({ assigneeId: 'u42' });
  });

  it('never sends an assignee filter for a Requester', async () => {
    setup('Requester');
    await state.setFilters({ assignee: 'unassigned', status: 'Open' });
    expect(lastQuery().unassigned).toBeUndefined();
    expect(lastQuery().assigneeId).toBeUndefined();
    expect(lastQuery().status).toBe('Open');
    expect(state.canFilterAssignee()).toBe(false);
  });

  it('paginates and applies the sort', async () => {
    setup('Agent');
    api.listTickets.mockReturnValue(of(page([summary('t2')], 45, 2)));
    await state.goToPage(2);
    expect(lastQuery().page).toBe(2);
    expect(state.page()).toBe(2);
    expect(state.total()).toBe(45);
    await state.setFilters({ sort: 'priority', direction: 'asc' });
    expect(lastQuery()).toMatchObject({ sort: 'priority', direction: 'asc', page: 1 });
  });

  it('resets filters', async () => {
    setup('Agent');
    await state.setFilters({ status: 'Open', q: 'x' });
    await state.reset();
    expect(state.hasActiveFilters()).toBe(false);
    expect(lastQuery().status).toBeUndefined();
  });

  it('reports a load error and recovers on retry', async () => {
    setup('Agent');
    api.listTickets.mockReturnValueOnce(
      throwError(() => new ApiError(500, 'Le serveur a rencontré une erreur.', null)),
    );
    await state.load();
    expect(state.status()).toBe('error');
    expect(state.error()).toContain('serveur');
    await state.load();
    expect(state.status()).toBe('ready');
  });

  it('ignores the answer of a superseded request', async () => {
    setup('Agent');
    const slow = new Subject<Page<TicketSummary>>();
    api.listTickets.mockReturnValueOnce(slow);
    const first = state.load();
    api.listTickets.mockReturnValueOnce(of(page([summary('fresh')])));
    await state.load();
    slow.next(page([summary('stale')]));
    slow.complete();
    await first;
    expect(state.items()[0].id).toBe('fresh');
  });
});
