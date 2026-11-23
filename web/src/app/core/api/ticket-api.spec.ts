import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { summary } from '../../testing/ticket-fixtures';
import { ApiClient } from './api-client';
import { allowedTransitions, isSlaBreached } from './ticket-rules';

describe('ApiClient (tickets)', () => {
  let api: ApiClient;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(ApiClient);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('sends only the filters that are set, with the contract parameter names', () => {
    api
      .listTickets({
        status: 'Open',
        unassigned: true,
        categoryId: '',
        q: 'vpn',
        sort: 'priority',
        direction: 'asc',
        page: 2,
        pageSize: 20,
      })
      .subscribe();
    const req = http.expectOne((r) => r.url === '/api/v1/tickets');
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('status')).toBe('Open');
    expect(req.request.params.get('unassigned')).toBe('true');
    expect(req.request.params.get('q')).toBe('vpn');
    expect(req.request.params.get('sort')).toBe('priority');
    expect(req.request.params.get('direction')).toBe('asc');
    expect(req.request.params.get('page')).toBe('2');
    expect(req.request.params.has('categoryId')).toBe(false);
    expect(req.request.params.has('assigneeId')).toBe(false);
    req.flush({ items: [], total: 0, page: 2, pageSize: 20 });
  });

  it('creates a ticket with POST /tickets', () => {
    api.createTicket({ title: 'T', description: 'D', priority: 'High' }).subscribe();
    const req = http.expectOne('/api/v1/tickets');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ title: 'T', description: 'D', priority: 'High' });
    req.flush({});
  });

  it('patches a ticket with its rowVersion', () => {
    api.updateTicket('t1', { rowVersion: '7', status: 'Open', assigneeId: null }).subscribe();
    const req = http.expectOne('/api/v1/tickets/t1');
    expect(req.request.method).toBe('PATCH');
    expect(req.request.body).toEqual({ rowVersion: '7', status: 'Open', assigneeId: null });
    req.flush({});
  });

  it('posts comments to the comments route', () => {
    api.addComment('t1', { body: 'Bonjour', isInternal: true }).subscribe();
    const req = http.expectOne('/api/v1/tickets/t1/comments');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ body: 'Bonjour', isInternal: true });
    req.flush({});
  });

  it('reads events, categories, assignees and one ticket', () => {
    api.listTicketEvents('t1').subscribe();
    api.listCategories().subscribe();
    api.listAssignees().subscribe();
    api.getTicket('t1').subscribe();
    http.expectOne('/api/v1/tickets/t1/events').flush([]);
    http.expectOne('/api/v1/categories').flush([]);
    http.expectOne('/api/v1/assignees').flush([]);
    http.expectOne('/api/v1/tickets/t1').flush({});
  });
});

describe('ticket rules', () => {
  it('mirrors the contract transitions', () => {
    expect(allowedTransitions('New')).toEqual(['Open']);
    expect(allowedTransitions('Open')).toEqual(['Pending', 'Resolved']);
    expect(allowedTransitions('Pending')).toEqual(['Open']);
    expect(allowedTransitions('Resolved')).toEqual(['Open', 'Closed']);
    expect(allowedTransitions('Closed')).toEqual([]);
  });

  it('flags a breached SLA only while the clock runs', () => {
    const now = new Date('2026-11-02T00:00:00Z');
    const past = '2026-11-01T12:00:00Z';
    const breached = (overrides: Parameters<typeof summary>[1]) =>
      isSlaBreached(summary('1', overrides), now);
    expect(breached({ status: 'Open', resolutionDueAt: past })).toBe(true);
    expect(breached({ status: 'Pending', resolutionDueAt: past })).toBe(false);
    expect(breached({ status: 'Resolved', resolutionDueAt: past })).toBe(false);
    expect(breached({ status: 'New', responseDueAt: past })).toBe(true);
    expect(breached({ status: 'Open', responseDueAt: past })).toBe(false);
    expect(breached({ status: 'Open', resolutionDueAt: '2026-11-03T00:00:00Z' })).toBe(false);
  });
});
