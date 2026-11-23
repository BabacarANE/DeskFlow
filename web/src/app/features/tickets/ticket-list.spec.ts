import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError } from '../../core/api/api-error';
import { Page, Role, TicketSummary } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { summary } from '../../testing/ticket-fixtures';
import { TicketList } from './ticket-list';

const page = (items: TicketSummary[], total = items.length, n = 1): Page<TicketSummary> => ({
  items,
  total,
  page: n,
  pageSize: 20,
});

describe('TicketList', () => {
  const api = { listTickets: vi.fn(), listCategories: vi.fn(), listAssignees: vi.fn() };
  let fixture: ComponentFixture<TicketList>;
  const el = () => fixture.nativeElement as HTMLElement;
  const button = (text: string) =>
    Array.from(el().querySelectorAll('button')).find((b) => b.textContent?.includes(text));
  const select = (id: string) => el().querySelector(`#${id}`) as HTMLSelectElement | null;
  const lastQuery = () => api.listTickets.mock.calls.at(-1)?.[0];

  async function settle() {
    await fixture.whenStable();
    // Promise chains of firstValueFrom() resolve outside Angular's pending-task tracking.
    await new Promise((resolve) => setTimeout(resolve));
    fixture.detectChanges();
  }

  async function render(role: Role = 'Agent') {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: ApiClient, useValue: api },
        { provide: AuthService, useValue: { role: () => role, user: () => ({ id: 'u-me' }) } },
      ],
    });
    fixture = TestBed.createComponent(TicketList);
    fixture.detectChanges();
    await settle();
  }

  async function choose(id: string, value: string) {
    const control = select(id) as HTMLSelectElement;
    control.value = value;
    control.dispatchEvent(new Event('change'));
    await settle();
  }

  beforeEach(() => {
    api.listTickets.mockReset();
    api.listCategories.mockReset();
    api.listAssignees.mockReset();
    api.listTickets.mockReturnValue(of(page([])));
    api.listCategories.mockReturnValue(of([{ id: 'c1', name: 'Matériel' }]));
    api.listAssignees.mockReturnValue(of([{ id: 'a1', displayName: 'Adam Agent' }]));
  });

  it('shows a loading state, then the tickets', async () => {
    const pending = new Subject<Page<TicketSummary>>();
    api.listTickets.mockReturnValue(pending);
    await render();
    expect(el().querySelector('[role="status"]')?.textContent).toContain('Chargement');
    pending.next(page([summary('t1', { title: 'VPN en panne' })]));
    pending.complete();
    await settle();
    expect(el().querySelectorAll('tbody tr')).toHaveLength(1);
    expect(el().textContent).toContain('VPN en panne');
  });

  it('shows an empty state, and a different one when filters are active', async () => {
    await render();
    expect(el().textContent).toContain('Aucun ticket pour le moment.');
    await choose('filter-status', 'Open');
    expect(el().textContent).toContain('Aucun ticket ne correspond à ces filtres.');
  });

  it('shows an error state and retries', async () => {
    api.listTickets.mockReturnValueOnce(
      throwError(() => new ApiError(500, 'Le serveur a rencontré une erreur.', null)),
    );
    await render();
    expect(el().querySelector('[role="alert"]')?.textContent).toContain('serveur');
    api.listTickets.mockReturnValue(of(page([summary('t1')])));
    button('Réessayer')?.click();
    await settle();
    expect(el().querySelectorAll('tbody tr')).toHaveLength(1);
  });

  it('has one accent button, "Nouveau ticket", linking to /tickets/new', async () => {
    api.listTickets.mockReturnValue(of(page([summary('t1')])));
    await render();
    const accents = el().querySelectorAll('.btn-accent');
    expect(accents).toHaveLength(1);
    expect(accents[0].textContent).toContain('Nouveau ticket');
    expect(accents[0].getAttribute('href')).toBe('/tickets/new');
  });

  it('renders accessible badges with text, and flags the SLA breach and Urgent', async () => {
    api.listTickets.mockReturnValue(
      of(
        page([
          summary('t1', {
            priority: 'Urgent',
            status: 'Open',
            resolutionDueAt: '2020-01-01T00:00:00Z',
          }),
          summary('t2', { priority: 'Low', status: 'Pending' }),
        ]),
      ),
    );
    await render();
    const rows = el().querySelectorAll('tbody tr');
    expect(rows[0].textContent).toContain('Urgente');
    expect(rows[0].textContent).toContain('Ouvert');
    expect(rows[0].textContent).toContain('Échéance SLA dépassée');
    expect(rows[0].querySelectorAll('.badge-accent')).toHaveLength(2);
    expect(rows[1].textContent).toContain('En attente du demandeur');
    expect(rows[1].textContent).not.toContain('Échéance SLA dépassée');
    expect(rows[1].querySelector('.badge-accent')).toBeNull();
  });

  it('shows the SLA alert to agents only', async () => {
    const late = summary('t1', { status: 'Open', resolutionDueAt: '2020-01-01T00:00:00Z' });
    api.listTickets.mockReturnValue(of(page([late])));
    await render('Agent');
    expect(el().textContent).toContain('Échéance SLA dépassée');
    TestBed.resetTestingModule();
    await render('Requester');
    expect(el().textContent).not.toContain('Échéance SLA dépassée');
  });

  it('sends status, priority, category and assignee filters to the API', async () => {
    await render();
    await choose('filter-status', 'Resolved');
    await choose('filter-priority', 'High');
    await choose('filter-category', 'c1');
    await choose('filter-assignee', 'unassigned');
    expect(lastQuery()).toMatchObject({
      status: 'Resolved',
      priority: 'High',
      categoryId: 'c1',
      unassigned: true,
      page: 1,
    });
    await choose('filter-assignee', 'me');
    expect(lastQuery()).toMatchObject({ assigneeId: 'u-me' });
    expect(lastQuery().unassigned).toBeUndefined();
    await choose('filter-assignee', 'a1');
    expect(lastQuery()).toMatchObject({ assigneeId: 'a1' });
  });

  it('offers the assignees "Moi", "Non assigné" and the colleagues', async () => {
    await render();
    const labels = Array.from((select('filter-assignee') as HTMLSelectElement).options).map((o) =>
      o.textContent?.trim(),
    );
    expect(labels).toEqual(['Tout le monde', 'Moi', 'Non assigné', 'Adam Agent']);
  });

  it('searches on submit and sorts', async () => {
    await render();
    const input = el().querySelector('#filter-q') as HTMLInputElement;
    input.value = 'imprimante';
    el().querySelector('form')?.dispatchEvent(new Event('submit'));
    await settle();
    expect(lastQuery().q).toBe('imprimante');
    await choose('filter-sort', 'priority');
    await choose('filter-direction', 'asc');
    expect(lastQuery()).toMatchObject({ q: 'imprimante', sort: 'priority', direction: 'asc' });
  });

  it('paginates', async () => {
    api.listTickets.mockReturnValue(of(page([summary('t1')], 45)));
    await render();
    expect(el().textContent).toContain('Page 1 sur 3');
    api.listTickets.mockReturnValue(of(page([summary('t2')], 45, 2)));
    button('Suivant')?.click();
    await settle();
    expect(lastQuery().page).toBe(2);
    expect(el().textContent).toContain('Page 2 sur 3');
  });

  it('resets the filters', async () => {
    await render();
    await choose('filter-status', 'Open');
    button('Réinitialiser les filtres')?.click();
    await settle();
    expect(lastQuery().status).toBeUndefined();
    expect(button('Réinitialiser les filtres')).toBeUndefined();
  });

  it('gives a Requester no assignee filter and no assignee loading', async () => {
    api.listTickets.mockReturnValue(of(page([summary('t1')])));
    await render('Requester');
    expect(select('filter-assignee')).toBeNull();
    expect(api.listAssignees).not.toHaveBeenCalled();
    const headers = Array.from(el().querySelectorAll('th')).map((h) => h.textContent?.trim());
    expect(headers).not.toContain('Assigné à');
  });

  it('labels every filter control', async () => {
    await render();
    const controls = el().querySelectorAll('select, input[type="search"]');
    expect(controls.length).toBeGreaterThanOrEqual(7);
    for (const control of Array.from(controls)) {
      expect(el().querySelector(`label[for="${control.id}"]`)).not.toBeNull();
    }
  });
});
