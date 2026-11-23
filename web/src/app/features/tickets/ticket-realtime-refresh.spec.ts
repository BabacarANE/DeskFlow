import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Subject, of } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { AuthService } from '../../core/auth/auth.service';
import { RealtimeEvent } from '../../core/realtime/realtime-events';
import { RealtimeService } from '../../core/realtime/realtime.service';
import { detail, summary } from '../../testing/ticket-fixtures';
import { TicketDetail } from './ticket-detail';
import { TicketList } from './ticket-list';

const event = (ticketId: string): RealtimeEvent => ({
  kind: 'ticketReplied',
  ticketId,
  number: 1,
  title: 'VPN',
  at: '2026-11-23T10:00:00Z',
});

describe('Targeted refresh on real-time events', () => {
  const events = new Subject<RealtimeEvent>();
  const api = {
    listTickets: vi.fn(),
    listCategories: vi.fn(),
    listAssignees: vi.fn(),
    getTicket: vi.fn(),
    listTicketEvents: vi.fn(),
    updateTicket: vi.fn(),
  };
  const auth = { role: () => 'Agent', user: () => ({ id: 'a1', displayName: 'Adam' }) };

  function configure() {
    for (const fn of Object.values(api)) fn.mockReset();
    api.listCategories.mockReturnValue(of([]));
    api.listAssignees.mockReturnValue(of([]));
    api.listTicketEvents.mockReturnValue(of([]));
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: ApiClient, useValue: api },
        { provide: AuthService, useValue: auth },
        { provide: RealtimeService, useValue: { events } },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: convertToParamMap({ id: 't1' }) } },
        },
      ],
    });
  }

  async function settle(fixture: ComponentFixture<unknown>) {
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve));
    fixture.detectChanges();
  }

  describe('ticket detail', () => {
    let fixture: ComponentFixture<TicketDetail>;
    const el = () => fixture.nativeElement as HTMLElement;

    beforeEach(async () => {
      configure();
      api.getTicket.mockReturnValue(of(detail('t1', { rowVersion: '1' })));
      fixture = TestBed.createComponent(TicketDetail);
      fixture.detectChanges();
      await settle(fixture);
    });

    it('refreshes quietly for its own ticket, keeping the reply being typed', async () => {
      const textarea = el().querySelector('#reply-body') as HTMLTextAreaElement;
      textarea.value = 'Réponse en cours de rédaction';
      textarea.dispatchEvent(new Event('input'));
      api.getTicket.mockReturnValue(of(detail('t1', { title: 'Titre à jour', rowVersion: '2' })));

      events.next(event('t1'));
      await settle(fixture);

      expect(api.getTicket).toHaveBeenCalledTimes(2);
      expect(el().querySelector('h1')?.textContent).toContain('Titre à jour');
      expect((el().querySelector('#reply-body') as HTMLTextAreaElement).value).toBe(
        'Réponse en cours de rédaction',
      );
      expect(el().querySelector('[role="alert"]')).toBeNull();
    });

    it('uses the refreshed rowVersion for the next change', async () => {
      api.getTicket.mockReturnValue(of(detail('t1', { rowVersion: '2' })));
      events.next(event('t1'));
      await settle(fixture);
      api.updateTicket.mockReturnValue(of(detail('t1', { rowVersion: '3', status: 'Pending' })));
      const pending = Array.from(el().querySelectorAll('button')).find((b) =>
        b.textContent?.includes('En attente'),
      );
      pending?.click();
      await settle(fixture);
      expect(api.updateTicket).toHaveBeenCalledWith(
        't1',
        expect.objectContaining({ rowVersion: '2' }),
      );
    });

    it('ignores events about other tickets', async () => {
      events.next(event('other'));
      await settle(fixture);
      expect(api.getTicket).toHaveBeenCalledTimes(1);
    });

    it('keeps the ticket on screen when the refresh fails', async () => {
      api.getTicket.mockReturnValue(new Subject().asObservable());
      api.getTicket.mockImplementation(() => {
        throw new Error('network');
      });
      events.next(event('t1'));
      await settle(fixture);
      expect(el().querySelector('h1')?.textContent).toContain('Ticket t1');
    });
  });

  describe('ticket list', () => {
    let fixture: ComponentFixture<TicketList>;
    const el = () => fixture.nativeElement as HTMLElement;

    beforeEach(async () => {
      configure();
      api.listTickets.mockReturnValue(
        of({ items: [summary('t1')], total: 1, page: 1, pageSize: 20 }),
      );
      fixture = TestBed.createComponent(TicketList);
      fixture.detectChanges();
      await settle(fixture);
    });

    it('reloads the same query without the loading state, keeping filters and typed text', async () => {
      const search = el().querySelector('#filter-q') as HTMLInputElement;
      search.value = 'vpn en cours';
      const status = el().querySelector('#filter-status') as HTMLSelectElement;
      status.value = 'Open';
      status.dispatchEvent(new Event('change'));
      await settle(fixture);
      const callsBefore = api.listTickets.mock.calls.length;
      api.listTickets.mockReturnValue(
        of({ items: [summary('t1'), summary('t2')], total: 2, page: 1, pageSize: 20 }),
      );
      vi.useFakeTimers();
      try {
        events.next(event('t2'));
        events.next(event('t2'));
        await vi.advanceTimersByTimeAsync(500);
      } finally {
        vi.useRealTimers();
      }
      await settle(fixture);

      expect(api.listTickets.mock.calls.length).toBe(callsBefore + 1); // debounced to one call
      expect(api.listTickets.mock.calls.at(-1)?.[0]).toMatchObject({ status: 'Open' });
      expect(el().querySelectorAll('tbody tr')).toHaveLength(2);
      expect((el().querySelector('#filter-q') as HTMLInputElement).value).toBe('vpn en cours');
      expect((el().querySelector('#filter-status') as HTMLSelectElement).value).toBe('Open');
    });
  });
});
