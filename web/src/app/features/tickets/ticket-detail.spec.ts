import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError } from '../../core/api/api-error';
import { Comment, Role, TicketDetail as Ticket, TicketEvent } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { detail } from '../../testing/ticket-fixtures';
import { TicketDetail } from './ticket-detail';

const comment = (id: string, overrides: Partial<Comment> = {}): Comment => ({
  id,
  author: { id: 'a1', displayName: 'Adam Agent' },
  body: `Message ${id}`,
  isInternal: false,
  createdAt: '2026-11-01T09:30:00Z',
  ...overrides,
});

const EVENTS: TicketEvent[] = [
  {
    id: 'e1',
    type: 'Created',
    actor: { id: 'r1', displayName: 'Dana' },
    at: '2026-11-01T09:00:00Z',
  },
  {
    id: 'e2',
    type: 'StatusChanged',
    actor: { id: 'a1', displayName: 'Adam Agent' },
    oldValue: 'New',
    newValue: 'Open',
    at: '2026-11-01T09:10:00Z',
  },
];

function problem(status: number, type?: string) {
  return new ApiError(status, 'Conflit', type ? { type, status } : null);
}

describe('TicketDetail', () => {
  const api = {
    getTicket: vi.fn(),
    updateTicket: vi.fn(),
    addComment: vi.fn(),
    listTicketEvents: vi.fn(),
    listCategories: vi.fn(),
    listAssignees: vi.fn(),
  };
  let fixture: ComponentFixture<TicketDetail>;
  const el = () => fixture.nativeElement as HTMLElement;
  const button = (text: string) =>
    Array.from(el().querySelectorAll('button')).find((b) => b.textContent?.includes(text));

  async function settle() {
    await fixture.whenStable();
    // Promise chains of firstValueFrom() resolve outside Angular's pending-task tracking.
    await new Promise((resolve) => setTimeout(resolve));
    fixture.detectChanges();
  }

  async function render(role: Role, ticket: Ticket) {
    api.getTicket.mockReturnValue(of(ticket));
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: ApiClient, useValue: api },
        {
          provide: AuthService,
          useValue: { role: () => role, user: () => ({ id: 'a1', displayName: 'Adam Agent' }) },
        },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: convertToParamMap({ id: 't1' }) } },
        },
      ],
    });
    fixture = TestBed.createComponent(TicketDetail);
    fixture.detectChanges();
    await settle();
  }

  beforeEach(() => {
    for (const fn of Object.values(api)) fn.mockReset();
    api.listTicketEvents.mockReturnValue(of(EVENTS));
    api.listCategories.mockReturnValue(of([{ id: 'c1', name: 'Matériel' }]));
    api.listAssignees.mockReturnValue(
      of([
        { id: 'a1', displayName: 'Adam Agent' },
        { id: 'a2', displayName: 'Léa Martin' },
      ]),
    );
  });

  it('shows a loading state, then the ticket', async () => {
    const pending = new Subject<Ticket>();
    api.getTicket.mockReturnValue(pending);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: ApiClient, useValue: api },
        { provide: AuthService, useValue: { role: () => 'Agent', user: () => ({ id: 'a1' }) } },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: convertToParamMap({ id: 't1' }) } },
        },
      ],
    });
    fixture = TestBed.createComponent(TicketDetail);
    fixture.detectChanges();
    expect(el().querySelector('[role="status"]')?.textContent).toContain('Chargement');
    pending.next(detail('t1', { title: 'VPN en panne' }));
    pending.complete();
    await settle();
    expect(el().querySelector('h1')?.textContent).toContain('VPN en panne');
  });

  it('says so when the ticket is not found (404)', async () => {
    api.getTicket.mockReturnValue(throwError(() => problem(404)));
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: ApiClient, useValue: api },
        { provide: AuthService, useValue: { role: () => 'Agent', user: () => ({ id: 'a1' }) } },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: convertToParamMap({ id: 'x' }) } },
        },
      ],
    });
    fixture = TestBed.createComponent(TicketDetail);
    fixture.detectChanges();
    await settle();
    expect(el().textContent).toContain('Ticket introuvable');
  });

  describe('as an Agent', () => {
    const ticket = detail('t1', {
      status: 'Open',
      assignee: null,
      comments: [
        comment('c1', { author: { id: 'r1', displayName: 'Dana' }, body: 'Réponse publique' }),
        comment('c2', { isInternal: true, body: 'Note réservée' }),
      ],
    });

    it('shows public replies and visually distinct internal notes', async () => {
      await render('Agent', ticket);
      const items = el().querySelectorAll('#comments-title ~ ol > li');
      expect(items).toHaveLength(2);
      expect(items[0].getAttribute('data-internal')).toBe('false');
      expect(items[1].getAttribute('data-internal')).toBe('true');
      expect(items[1].textContent).toContain('Note interne');
      expect(items[1].className).toContain('border-dashed');
      expect(items[0].className).not.toContain('border-dashed');
      expect(items[0].textContent).not.toContain('Note interne');
    });

    it('lists only the valid transitions from the current status', async () => {
      await render('Agent', ticket);
      const labels = Array.from(
        el().querySelectorAll('[aria-label="Transitions possibles"] button'),
      ).map((b) => b.textContent?.trim());
      expect(labels).toEqual(['Passer à « En attente du demandeur »', 'Passer à « Résolu »']);
    });

    it('offers reopening and closing from Resolved', async () => {
      await render('Agent', detail('t1', { status: 'Resolved' }));
      const labels = Array.from(
        el().querySelectorAll('[aria-label="Transitions possibles"] button'),
      ).map((b) => b.textContent?.trim());
      expect(labels).toEqual(['Passer à « Ouvert »', 'Passer à « Fermé »']);
    });

    it('changes the status with the current rowVersion and refreshes the history', async () => {
      await render('Agent', detail('t1', { status: 'Open', rowVersion: '5' }));
      api.updateTicket.mockReturnValue(of(detail('t1', { status: 'Pending', rowVersion: '6' })));
      button('Passer à « En attente du demandeur »')?.click();
      await settle();
      expect(api.updateTicket).toHaveBeenCalledWith('t1', { status: 'Pending', rowVersion: '5' });
      expect(el().textContent).toContain('Passer à « Ouvert »');
      expect(api.listTicketEvents).toHaveBeenCalledTimes(2);
    });

    it('changes the priority and the category', async () => {
      await render('Agent', detail('t1', { rowVersion: '2' }));
      api.updateTicket.mockReturnValue(of(detail('t1', { priority: 'Urgent', rowVersion: '3' })));
      const priority = el().querySelector('#ticket-priority') as HTMLSelectElement;
      priority.value = 'Urgent';
      priority.dispatchEvent(new Event('change'));
      await settle();
      expect(api.updateTicket).toHaveBeenLastCalledWith('t1', {
        priority: 'Urgent',
        rowVersion: '2',
      });

      const category = el().querySelector('#ticket-category') as HTMLSelectElement;
      category.value = 'c1';
      category.dispatchEvent(new Event('change'));
      await settle();
      expect(api.updateTicket).toHaveBeenLastCalledWith('t1', {
        categoryId: 'c1',
        rowVersion: '3',
      });
    });

    it('assigns a colleague, unassigns, and assigns to me', async () => {
      await render('Agent', detail('t1', { rowVersion: '1', assignee: null }));
      api.updateTicket.mockReturnValue(of(detail('t1', { rowVersion: '2' })));
      const assignee = el().querySelector('#ticket-assignee') as HTMLSelectElement;
      expect(Array.from(assignee.options).map((o) => o.textContent?.trim())).toEqual([
        'Non assigné',
        'Adam Agent',
        'Léa Martin',
      ]);
      assignee.value = 'a2';
      assignee.dispatchEvent(new Event('change'));
      await settle();
      expect(api.updateTicket).toHaveBeenLastCalledWith('t1', {
        assigneeId: 'a2',
        rowVersion: '1',
      });

      button('M’assigner')?.click();
      await settle();
      expect(api.updateTicket).toHaveBeenLastCalledWith('t1', {
        assigneeId: 'a1',
        rowVersion: '2',
      });
    });

    it('hides "M’assigner" when the ticket is already mine', async () => {
      await render('Agent', detail('t1', { assignee: { id: 'a1', displayName: 'Adam Agent' } }));
      expect(button('M’assigner')).toBeUndefined();
    });

    it('reloads and offers to re-apply the change on a version conflict (409)', async () => {
      await render('Agent', detail('t1', { status: 'Open', rowVersion: '5' }));
      api.updateTicket.mockReturnValueOnce(
        throwError(() => problem(409, 'https://deskflow.example/problems/concurrency-conflict')),
      );
      api.getTicket.mockReturnValue(of(detail('t1', { status: 'Open', rowVersion: '6' })));
      button('Passer à « Résolu »')?.click();
      await settle();

      const alert = el().querySelector('[role="alert"]');
      expect(alert?.textContent).toContain('modifié par quelqu’un d’autre');
      expect(alert?.textContent).toContain('« Résolu »');
      expect(api.getTicket).toHaveBeenCalledTimes(2);

      api.updateTicket.mockReturnValue(of(detail('t1', { status: 'Resolved', rowVersion: '7' })));
      button('Réappliquer ma modification')?.click();
      await settle();
      expect(api.updateTicket).toHaveBeenLastCalledWith('t1', {
        status: 'Resolved',
        rowVersion: '6',
      });
      expect(el().textContent).not.toContain('Réappliquer ma modification');
    });

    it('can dismiss the conflict banner', async () => {
      await render('Agent', detail('t1', { rowVersion: '5' }));
      api.updateTicket.mockReturnValueOnce(
        throwError(() => problem(409, 'https://deskflow.example/problems/concurrency-conflict')),
      );
      button('Passer à « Résolu »')?.click();
      await settle();
      button('Ignorer')?.click();
      fixture.detectChanges();
      expect(el().textContent).not.toContain('Réappliquer ma modification');
    });

    it('explains an invalid transition (409) and reloads the ticket', async () => {
      await render('Agent', detail('t1', { status: 'Open', rowVersion: '5' }));
      api.updateTicket.mockReturnValueOnce(
        throwError(() => problem(409, 'https://deskflow.example/problems/invalid-transition')),
      );
      api.getTicket.mockReturnValue(of(detail('t1', { status: 'Resolved', rowVersion: '6' })));
      button('Passer à « Résolu »')?.click();
      await settle();
      const text = el().querySelector('[role="alert"]')?.textContent ?? '';
      expect(text).toContain('transition n’est pas autorisée');
      expect(text).toContain('« Résolu »');
      expect(el().textContent).not.toContain('Réappliquer ma modification');
    });

    it('puts the selector back when a change is refused', async () => {
      await render('Agent', detail('t1', { priority: 'Normal', rowVersion: '1' }));
      api.updateTicket.mockReturnValue(throwError(() => new ApiError(403, 'Interdit', null)));
      const priority = el().querySelector('#ticket-priority') as HTMLSelectElement;
      priority.value = 'Low';
      priority.dispatchEvent(new Event('change'));
      await settle();
      expect(priority.value).toBe('Normal');
      expect(el().querySelector('[role="alert"]')?.textContent).toContain('Interdit');
    });

    it('sends a public reply by default and an internal note when the box is checked', async () => {
      await render('Agent', ticket);
      api.addComment.mockReturnValue(of(comment('c9')));
      const body = el().querySelector('#reply-body') as HTMLTextAreaElement;
      body.value = ' Bonjour ';
      body.dispatchEvent(new Event('input'));
      el().querySelector('form')?.dispatchEvent(new Event('submit'));
      await settle();
      expect(api.addComment).toHaveBeenLastCalledWith('t1', { body: 'Bonjour', isInternal: false });
      expect(body.value).toBe('');

      body.value = 'Pour l’équipe';
      body.dispatchEvent(new Event('input'));
      const box = el().querySelector('input[type="checkbox"]') as HTMLInputElement;
      box.click();
      fixture.detectChanges();
      expect(button('Ajouter la note')).toBeDefined();
      el().querySelector('form')?.dispatchEvent(new Event('submit'));
      await settle();
      expect(api.addComment).toHaveBeenLastCalledWith('t1', {
        body: 'Pour l’équipe',
        isInternal: true,
      });
    });

    it('shows the history with readable labels', async () => {
      await render('Agent', ticket);
      const history = el().querySelector('#history-title')?.parentElement?.textContent ?? '';
      expect(history).toContain('Ticket créé');
      expect(history).toContain('Statut modifié');
      expect(history).toContain('Nouveau → Ouvert');
      expect(history).toContain('Adam Agent');
    });

    it('has a single accent button', async () => {
      await render('Agent', ticket);
      expect(el().querySelectorAll('.btn-accent')).toHaveLength(1);
    });
  });

  describe('as a Requester', () => {
    const ticket = detail('t1', {
      status: 'Open',
      comments: [comment('c1', { body: 'Réponse publique' })],
    });

    it('shows no handling panel and no internal-note checkbox', async () => {
      await render('Requester', ticket);
      expect(el().querySelector('#handling-title')).toBeNull();
      expect(el().querySelector('#ticket-priority')).toBeNull();
      expect(button('M’assigner')).toBeUndefined();
      expect(el().querySelector('input[type="checkbox"]')).toBeNull();
      expect(el().textContent).toContain('Réponse publique');
      expect(api.listAssignees).not.toHaveBeenCalled();
    });

    it('replies publicly, never as an internal note', async () => {
      await render('Requester', ticket);
      api.addComment.mockReturnValue(of(comment('c9')));
      const body = el().querySelector('#reply-body') as HTMLTextAreaElement;
      body.value = 'Merci !';
      body.dispatchEvent(new Event('input'));
      el().querySelector('form')?.dispatchEvent(new Event('submit'));
      await settle();
      expect(api.addComment).toHaveBeenCalledWith('t1', { body: 'Merci !', isInternal: false });
    });
  });

  describe('on a Closed ticket', () => {
    const closed = detail('t1', { status: 'Closed' });

    it('disables the reply with an explanation (Requester)', async () => {
      await render('Requester', closed);
      expect(el().querySelector('#reply-body')).toBeNull();
      expect(el().textContent).toContain('Ce ticket est fermé');
    });

    it('also offers an Agent no transition and no reply', async () => {
      await render('Agent', closed);
      expect(el().querySelector('#reply-body')).toBeNull();
      expect(el().textContent).toContain('Aucune transition possible');
      expect(el().querySelector('[aria-label="Transitions possibles"]')).toBeNull();
    });

    it('reloads when the server answers 409 to a reply sent just before closure', async () => {
      await render('Agent', detail('t1', { status: 'Resolved' }));
      api.addComment.mockReturnValue(throwError(() => problem(409, 'ticket-closed')));
      api.getTicket.mockReturnValue(of(closed));
      const body = el().querySelector('#reply-body') as HTMLTextAreaElement;
      body.value = 'Encore une chose';
      body.dispatchEvent(new Event('input'));
      el().querySelector('form')?.dispatchEvent(new Event('submit'));
      await settle();
      expect(el().textContent).toContain('Ce ticket est fermé');
    });
  });
});
