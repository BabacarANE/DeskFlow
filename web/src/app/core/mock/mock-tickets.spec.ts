import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../api/api-client';
import { ApiError } from '../api/api-error';
import { errorInterceptor } from '../api/error.interceptor';
import { TicketDetail } from '../api/models';
import {
  mockBackendInterceptor,
  resetMockBackend,
  setMockLatency,
} from './mock-backend.interceptor';

const ROLE_KEY = 'deskflow.mock.role';

/** The simulated backend must follow the frozen phase 2 contract (docs/api/openapi.json). */
describe('mock backend (tickets)', () => {
  let api: ApiClient;

  beforeEach(() => {
    sessionStorage.clear();
    setMockLatency(0);
    resetMockBackend();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([errorInterceptor, mockBackendInterceptor]))],
    });
    api = TestBed.inject(ApiClient);
  });

  afterEach(() => {
    sessionStorage.clear();
    setMockLatency(250);
  });

  const fail = async (promise: Promise<unknown>): Promise<ApiError> => {
    try {
      await promise;
    } catch (e) {
      return e as ApiError;
    }
    throw new Error('expected the request to fail');
  };

  const openTicket = async (): Promise<TicketDetail> => {
    const page = await firstValueFrom(api.listTickets({ status: 'Open', pageSize: 100 }));
    return firstValueFrom(api.getTicket(page.items[0].id));
  };

  it('paginates and reports the total', async () => {
    await firstValueFrom(api.demoLogin('agent'));
    const first = await firstValueFrom(api.listTickets({ page: 1, pageSize: 10 }));
    const second = await firstValueFrom(api.listTickets({ page: 2, pageSize: 10 }));
    expect(first.items).toHaveLength(10);
    expect(first.total).toBeGreaterThan(20);
    expect(second.items[0].id).not.toBe(first.items[0].id);
    expect((await fail(firstValueFrom(api.listTickets({ pageSize: 101 })))).status).toBe(422);
  });

  it('filters by status, priority, category, assignee and unassigned', async () => {
    await firstValueFrom(api.demoLogin('agent'));
    const open = await firstValueFrom(api.listTickets({ status: 'Open', pageSize: 100 }));
    expect(open.items.every((t) => t.status === 'Open')).toBe(true);
    const urgent = await firstValueFrom(api.listTickets({ priority: 'Urgent', pageSize: 100 }));
    expect(urgent.items.length).toBeGreaterThan(0);
    expect(urgent.items.every((t) => t.priority === 'Urgent')).toBe(true);

    const unassigned = await firstValueFrom(api.listTickets({ unassigned: true, pageSize: 100 }));
    expect(unassigned.items.length).toBeGreaterThan(0);
    expect(unassigned.items.every((t) => !t.assignee)).toBe(true);

    const me = await firstValueFrom(api.getMe());
    const mine = await firstValueFrom(api.listTickets({ assigneeId: me.id, pageSize: 100 }));
    expect(mine.items.length).toBeGreaterThan(0);
    expect(mine.items.every((t) => t.assignee?.id === me.id)).toBe(true);

    const [category] = await firstValueFrom(api.listCategories());
    const inCategory = await firstValueFrom(
      api.listTickets({ categoryId: category.id, pageSize: 100 }),
    );
    expect(inCategory.items.length).toBeGreaterThan(0);
    expect(inCategory.items.every((t) => t.category?.id === category.id)).toBe(true);
  });

  it('sorts and searches by title or number', async () => {
    await firstValueFrom(api.demoLogin('agent'));
    const asc = await firstValueFrom(api.listTickets({ sort: 'number', direction: 'asc' }));
    const numbers = asc.items.map((t) => t.number);
    expect(numbers).toEqual([...numbers].sort((a, b) => a - b));
    const byPriority = await firstValueFrom(
      api.listTickets({ sort: 'priority', direction: 'desc', pageSize: 5 }),
    );
    expect(byPriority.items[0].priority).toBe('Urgent');

    const found = await firstValueFrom(api.listTickets({ q: 'VPN' }));
    expect(found.items.length).toBeGreaterThan(0);
    expect(found.items.every((t) => t.title.toLowerCase().includes('vpn'))).toBe(true);
    const byNumber = await firstValueFrom(api.listTickets({ q: '3' }));
    expect(byNumber.items.some((t) => t.number === 3)).toBe(true);
  });

  it('shows a requester only their own tickets, ignores assignee filters, hides others with 404', async () => {
    await firstValueFrom(api.demoLogin('demandeur'));
    const me = await firstValueFrom(api.getMe());
    const all = await firstValueFrom(api.listTickets({ pageSize: 100 }));
    expect(all.items.length).toBeGreaterThan(0);
    expect(all.items.every((t) => t.requester.id === me.id)).toBe(true);
    const filtered = await firstValueFrom(api.listTickets({ unassigned: true, pageSize: 100 }));
    expect(filtered.total).toBe(all.total);

    sessionStorage.setItem(ROLE_KEY, 'agent');
    const everything = await firstValueFrom(api.listTickets({ pageSize: 100 }));
    const foreign = everything.items.find((t) => t.requester.id !== me.id);
    expect(foreign).toBeDefined();
    sessionStorage.setItem(ROLE_KEY, 'demandeur');
    expect((await fail(firstValueFrom(api.getTicket(foreign?.id ?? '')))).status).toBe(404);
  });

  it('refuses the anonymous visitor and the SuperAdmin', async () => {
    expect((await fail(firstValueFrom(api.listTickets()))).status).toBe(401);
    sessionStorage.setItem(ROLE_KEY, 'superadmin');
    expect((await fail(firstValueFrom(api.listTickets()))).status).toBe(403);
    const create = api.createTicket({ title: 'a', description: 'b' });
    expect((await fail(firstValueFrom(create))).status).toBe(403);
  });

  it('creates a ticket as New with a sequential number and 422 errors per field', async () => {
    await firstValueFrom(api.demoLogin('demandeur'));
    const before = await firstValueFrom(api.listTickets({ sort: 'number', direction: 'desc' }));
    const created = await firstValueFrom(
      api.createTicket({ title: '  Clavier cassé ', description: 'Touche A', priority: 'High' }),
    );
    expect(created.status).toBe('New');
    expect(created.title).toBe('Clavier cassé');
    expect(created.priority).toBe('High');
    expect(created.rowVersion).toBeTruthy();
    expect(created.responseDueAt).toBeTruthy();
    expect(created.number).toBe(before.items[0].number + 1);
    const after = await firstValueFrom(api.listTickets());
    expect(after.total).toBe(before.total + 1);

    const err = await fail(firstValueFrom(api.createTicket({ title: '', description: '' })));
    expect(err.status).toBe(422);
    expect(Object.keys(err.fieldErrors).sort()).toEqual(['description', 'title']);
    const unknown = await fail(
      firstValueFrom(api.createTicket({ title: 'a', description: 'b', categoryId: 'nope' })),
    );
    expect(unknown.fieldErrors['categoryId']).toBeDefined();
  });

  it('applies valid transitions, records events and rejects the others with 409', async () => {
    await firstValueFrom(api.demoLogin('agent'));
    const ticket = await openTicket();

    const bad = await fail(
      firstValueFrom(
        api.updateTicket(ticket.id, { rowVersion: ticket.rowVersion, status: 'Closed' }),
      ),
    );
    expect(bad.status).toBe(409);
    expect(bad.problem?.type?.endsWith('invalid-transition')).toBe(true);

    const pending = await firstValueFrom(
      api.updateTicket(ticket.id, { rowVersion: ticket.rowVersion, status: 'Pending' }),
    );
    expect(pending.status).toBe('Pending');
    expect(pending.rowVersion).not.toBe(ticket.rowVersion);

    const events = await firstValueFrom(api.listTicketEvents(ticket.id));
    expect(events[events.length - 1]).toMatchObject({
      type: 'StatusChanged',
      oldValue: 'Open',
      newValue: 'Pending',
      actor: { displayName: 'Adam Agent' },
    });
  });

  it('answers 409 concurrency-conflict for a stale rowVersion and requires it (422)', async () => {
    await firstValueFrom(api.demoLogin('agent'));
    const ticket = await openTicket();
    const other = ticket.priority === 'High' ? 'Low' : 'High';
    await firstValueFrom(
      api.updateTicket(ticket.id, { rowVersion: ticket.rowVersion, priority: other }),
    );
    const stale = await fail(
      firstValueFrom(
        api.updateTicket(ticket.id, { rowVersion: ticket.rowVersion, priority: 'Low' }),
      ),
    );
    expect(stale.status).toBe(409);
    expect(stale.problem?.type?.endsWith('concurrency-conflict')).toBe(true);
    const missing = await fail(firstValueFrom(api.updateTicket(ticket.id, { rowVersion: '' })));
    expect(missing.status).toBe(422);
  });

  it('assigns, unassigns and recategorises; refuses unknown assignees with 422', async () => {
    await firstValueFrom(api.demoLogin('agent'));
    const assignees = await firstValueFrom(api.listAssignees());
    const names = assignees.map((a) => a.displayName);
    expect(names).toContain('Léa Martin');
    expect(names).not.toContain('Dana Demandeur');

    let ticket = await openTicket();
    ticket = await firstValueFrom(
      api.updateTicket(ticket.id, { rowVersion: ticket.rowVersion, assigneeId: assignees[0].id }),
    );
    expect(ticket.assignee?.id).toBe(assignees[0].id);
    ticket = await firstValueFrom(
      api.updateTicket(ticket.id, {
        rowVersion: ticket.rowVersion,
        assigneeId: null,
        categoryId: null,
      }),
    );
    expect(ticket.assignee).toBeNull();
    expect(ticket.category).toBeNull();
    const err = await fail(
      firstValueFrom(
        api.updateTicket(ticket.id, { rowVersion: ticket.rowVersion, assigneeId: 'nope' }),
      ),
    );
    expect(err.status).toBe(422);
  });

  it('keeps assignees and PATCH for agents only', async () => {
    await firstValueFrom(api.demoLogin('demandeur'));
    expect((await fail(firstValueFrom(api.listAssignees()))).status).toBe(403);
    const mine = await firstValueFrom(api.listTickets({ pageSize: 1 }));
    const patch = api.updateTicket(mine.items[0].id, { rowVersion: '1', priority: 'Low' });
    expect((await fail(firstValueFrom(patch))).status).toBe(403);
  });

  it('hides internal comments from the requester and forbids writing them', async () => {
    await firstValueFrom(api.demoLogin('demandeur'));
    const me = await firstValueFrom(api.getMe());
    const own = (await firstValueFrom(api.listTickets({ status: 'Open', pageSize: 100 }))).items[0];

    sessionStorage.setItem(ROLE_KEY, 'agent');
    await firstValueFrom(api.addComment(own.id, { body: 'Note secrète', isInternal: true }));
    const asAgent = await firstValueFrom(api.getTicket(own.id));
    expect(asAgent.comments.some((c) => c.isInternal)).toBe(true);

    sessionStorage.setItem(ROLE_KEY, 'demandeur');
    const asRequester = await firstValueFrom(api.getTicket(own.id));
    expect(asRequester.requester.id).toBe(me.id);
    expect(asRequester.comments.length).toBeGreaterThan(0);
    expect(asRequester.comments.every((c) => !c.isInternal)).toBe(true);
    const internal = api.addComment(own.id, { body: 'x', isInternal: true });
    expect((await fail(firstValueFrom(internal))).status).toBe(403);
  });

  it('sets firstResponseAt on the first public agent comment and reopens Pending on a requester reply', async () => {
    await firstValueFrom(api.demoLogin('demandeur'));
    const own = (await firstValueFrom(api.listTickets({ status: 'New', pageSize: 100 }))).items[0];

    sessionStorage.setItem(ROLE_KEY, 'agent');
    const fresh = await firstValueFrom(api.getTicket(own.id));
    expect(fresh.firstResponseAt ?? null).toBeNull();
    await firstValueFrom(api.addComment(fresh.id, { body: 'Bonjour' }));
    expect((await firstValueFrom(api.getTicket(fresh.id))).firstResponseAt).toBeTruthy();

    const open = await firstValueFrom(
      api.updateTicket(fresh.id, { rowVersion: fresh.rowVersion, status: 'Open' }),
    );
    const pending = await firstValueFrom(
      api.updateTicket(open.id, { rowVersion: open.rowVersion, status: 'Pending' }),
    );

    sessionStorage.setItem(ROLE_KEY, 'demandeur');
    await firstValueFrom(api.addComment(pending.id, { body: 'Voici les informations' }));
    expect((await firstValueFrom(api.getTicket(pending.id))).status).toBe('Open');
    const events = await firstValueFrom(api.listTicketEvents(pending.id));
    expect(events[events.length - 1]).toMatchObject({ newValue: 'Open', oldValue: 'Pending' });
  });

  it('refuses an empty comment (422) and a comment on a Closed ticket (409)', async () => {
    await firstValueFrom(api.demoLogin('agent'));
    const closed = (await firstValueFrom(api.listTickets({ status: 'Closed', pageSize: 1 })))
      .items[0];
    const err = await fail(firstValueFrom(api.addComment(closed.id, { body: 'Encore ?' })));
    expect(err.status).toBe(409);
    const open = await openTicket();
    const empty = await fail(firstValueFrom(api.addComment(open.id, { body: '  ' })));
    expect(empty.status).toBe(422);
    expect(empty.fieldErrors['body']).toBeDefined();
  });

  it('lists categories sorted by name', async () => {
    await firstValueFrom(api.demoLogin('demandeur'));
    const names = (await firstValueFrom(api.listCategories())).map((c) => c.name);
    expect(names.length).toBeGreaterThan(0);
    expect(names).toEqual([...names].sort((a, b) => a.localeCompare(b)));
  });
});
