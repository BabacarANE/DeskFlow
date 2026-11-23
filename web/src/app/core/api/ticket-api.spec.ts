import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { detail, summary } from '../../testing/ticket-fixtures';
import { ApiClient } from './api-client';
import { allowedTransitions, isSlaBreached, isSlaBreachedDetail } from './ticket-rules';

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

  describe('SLA alert', () => {
    const now = new Date('2026-11-02T00:00:00Z');
    const past = '2026-11-01T12:00:00Z';
    const future = '2026-11-03T00:00:00Z';
    const list = (overrides: Parameters<typeof summary>[1]) =>
      isSlaBreached(summary('1', overrides), now);
    const full = (overrides: Parameters<typeof detail>[1]) =>
      isSlaBreachedDetail(detail('1', overrides), now);

    it('list: resolution deadline passed while New or Open', () => {
      expect(list({ status: 'Open', resolutionDueAt: past })).toBe(true);
      expect(list({ status: 'New', resolutionDueAt: past })).toBe(true);
      expect(list({ status: 'Open', resolutionDueAt: future })).toBe(false);
    });

    it('list: never uses the response deadline (no false positive after a public reply)', () => {
      expect(list({ status: 'Open', responseDueAt: past, resolutionDueAt: future })).toBe(false);
      expect(list({ status: 'New', responseDueAt: past, resolutionDueAt: future })).toBe(false);
    });

    it('list: an escalated New or Open ticket is flagged, a Resolved one is not', () => {
      expect(list({ status: 'Open', escalatedAt: past, resolutionDueAt: future })).toBe(true);
      expect(list({ status: 'Resolved', escalatedAt: past, resolutionDueAt: past })).toBe(false);
      expect(list({ status: 'Closed', escalatedAt: past })).toBe(false);
    });

    it('list: Pending suspends the SLA, nothing is flagged', () => {
      expect(list({ status: 'Pending', resolutionDueAt: past, escalatedAt: past })).toBe(false);
    });

    it('detail: Open without any answer and a response deadline passed', () => {
      expect(
        full({
          status: 'Open',
          firstResponseAt: null,
          responseDueAt: past,
          resolutionDueAt: future,
        }),
      ).toBe(true);
    });

    it('detail: a public reply was given, so the response deadline is no longer late', () => {
      expect(
        full({
          status: 'New',
          firstResponseAt: '2026-11-01T11:00:00Z',
          responseDueAt: past,
          resolutionDueAt: future,
        }),
      ).toBe(false);
    });

    it('detail: resolution passed, escalated, Resolved and Pending cases', () => {
      expect(full({ status: 'Open', firstResponseAt: past, resolutionDueAt: past })).toBe(true);
      expect(full({ status: 'Open', firstResponseAt: past, escalatedAt: past })).toBe(true);
      expect(full({ status: 'Resolved', escalatedAt: past, resolutionDueAt: past })).toBe(false);
      expect(full({ status: 'Pending', responseDueAt: past, resolutionDueAt: past })).toBe(false);
    });
  });
});
