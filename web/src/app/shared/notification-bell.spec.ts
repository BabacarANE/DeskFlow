import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Subject } from 'rxjs';
import { AuthService } from '../core/auth/auth.service';
import { RealtimeEvent } from '../core/realtime/realtime-events';
import { RealtimeService, RealtimeStatus } from '../core/realtime/realtime.service';
import { NotificationBell } from './notification-bell';

const created = (number: number): RealtimeEvent => ({
  kind: 'ticketCreated',
  ticketId: `t${number}`,
  number,
  title: `Titre ${number}`,
  priority: 'Normal',
  at: '2026-11-23T10:00:00Z',
});

describe('NotificationBell', () => {
  const events = new Subject<RealtimeEvent>();
  const status = signal<RealtimeStatus>('connected');
  let fixture: ComponentFixture<NotificationBell>;
  const trigger = () => fixture.nativeElement.querySelector('button') as HTMLButtonElement;
  const panel = () => document.querySelector('[role="dialog"]') as HTMLElement | null;
  const items = () =>
    Array.from(document.querySelectorAll<HTMLButtonElement>('[role="dialog"] li button'));

  async function settle() {
    fixture.detectChanges();
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve));
    fixture.detectChanges();
  }

  async function openPanel() {
    trigger().click();
    await settle();
  }

  const press = (key: string, init: KeyboardEventInit = {}) =>
    (document.activeElement ?? document.body).dispatchEvent(
      new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init }),
    );

  beforeEach(async () => {
    status.set('connected');
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: RealtimeService, useValue: { events, status } },
        { provide: AuthService, useValue: { user: signal({ id: 'u1' }) } },
      ],
    });
    fixture = TestBed.createComponent(NotificationBell);
    await settle();
  });

  afterEach(() => {
    fixture.destroy();
  });

  it('exposes the unread count as text, not as a colour', async () => {
    expect(trigger().getAttribute('aria-label')).toBe('Notifications, aucune non lue');
    events.next(created(1));
    events.next(created(2));
    await settle();
    expect(trigger().getAttribute('aria-label')).toBe('Notifications, 2 non lues');
    expect(trigger().textContent).toContain('2');
    expect(trigger().className).not.toContain('btn-accent');
  });

  it('announces arrivals politely', async () => {
    events.next(created(7));
    await settle();
    const live = fixture.nativeElement.querySelector('[aria-live="polite"]') as HTMLElement;
    expect(live.textContent).toContain('Nouvelle notification : Nouveau ticket #7 : Titre 7');
  });

  it('shows an empty state', async () => {
    await openPanel();
    expect(panel()?.textContent).toContain('Aucune notification');
    expect(trigger().getAttribute('aria-expanded')).toBe('true');
  });

  it('moves focus into the panel, navigates with arrows and closes on Escape', async () => {
    events.next(created(1));
    events.next(created(2));
    await settle();
    await openPanel();
    const [first, second] = items();
    expect(first.textContent).toContain('#2');
    expect(document.activeElement?.textContent).toContain('Tout marquer');
    press('ArrowDown');
    expect(document.activeElement).toBe(first);
    press('ArrowDown');
    expect(document.activeElement).toBe(second);
    press('ArrowUp');
    expect(document.activeElement).toBe(first);
    press('Escape');
    await settle();
    expect(panel()).toBeNull();
    expect(document.activeElement).toBe(trigger());
    expect(trigger().getAttribute('aria-expanded')).toBe('false');
  });

  it('marks everything as read', async () => {
    events.next(created(1));
    await settle();
    await openPanel();
    const markAll = Array.from(document.querySelectorAll('[role="dialog"] button')).find((b) =>
      b.textContent?.includes('Tout marquer comme lu'),
    ) as HTMLButtonElement;
    markAll.click();
    await settle();
    expect(trigger().getAttribute('aria-label')).toBe('Notifications, aucune non lue');
    expect(panel()?.textContent).not.toContain('Non lue');
  });

  it('opens the ticket of a notification and closes the panel', async () => {
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    events.next(created(12));
    await settle();
    await openPanel();
    items()[0].click();
    await settle();
    expect(navigate).toHaveBeenCalledWith(['/tickets', 't12']);
    expect(panel()).toBeNull();
    expect(trigger().getAttribute('aria-label')).toBe('Notifications, aucune non lue');
  });

  it('mentions the offline state without blocking', async () => {
    status.set('offline');
    await settle();
    expect(trigger().getAttribute('aria-label')).toContain('hors ligne');
    await openPanel();
    expect(panel()?.textContent).toContain('Temps réel indisponible');
  });
});
