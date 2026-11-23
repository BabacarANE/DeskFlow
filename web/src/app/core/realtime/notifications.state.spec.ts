import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { AuthService } from '../auth/auth.service';
import { MAX_NOTIFICATIONS, NotificationsState } from './notifications.state';
import { RealtimeEvent } from './realtime-events';
import { RealtimeService } from './realtime.service';

const event = (kind: RealtimeEvent['kind'], number = 12): RealtimeEvent =>
  ({
    kind,
    ticketId: `t${number}`,
    number,
    title: 'VPN en panne',
    priority: 'High',
    at: '2026-11-23T10:00:00Z',
  }) as RealtimeEvent;

describe('NotificationsState', () => {
  const events = new Subject<RealtimeEvent>();
  const user = signal<{ id: string } | null>({ id: 'u1' });
  let state: NotificationsState;

  beforeEach(() => {
    user.set({ id: 'u1' });
    TestBed.configureTestingModule({
      providers: [
        { provide: RealtimeService, useValue: { events } },
        { provide: AuthService, useValue: { user } },
      ],
    });
    state = TestBed.inject(NotificationsState);
    TestBed.tick();
  });

  it('writes the three notification texts of the spec', () => {
    events.next(event('ticketCreated'));
    events.next(event('ticketAssigned'));
    events.next(event('ticketReplied'));
    expect(state.items().map((n) => n.text)).toEqual([
      'Le demandeur a répondu sur #12 : VPN en panne',
      'Ticket #12 vous a été assigné',
      'Nouveau ticket #12 : VPN en panne',
    ]);
  });

  it('counts unread notifications and supports read, then read all', () => {
    events.next(event('ticketCreated', 1));
    events.next(event('ticketCreated', 2));
    events.next(event('ticketCreated', 3));
    expect(state.unreadCount()).toBe(3);
    state.markRead(state.items()[0].id);
    expect(state.unreadCount()).toBe(2);
    state.markAllRead();
    expect(state.unreadCount()).toBe(0);
    expect(state.items()).toHaveLength(3);
  });

  it('announces the latest arrival', () => {
    events.next(event('ticketAssigned'));
    expect(state.announcement()).toBe('Nouvelle notification : Ticket #12 vous a été assigné');
  });

  it('keeps only the most recent notifications', () => {
    for (let i = 1; i <= MAX_NOTIFICATIONS + 5; i++) events.next(event('ticketCreated', i));
    expect(state.items()).toHaveLength(MAX_NOTIFICATIONS);
    expect(state.items()[0].text).toContain(`#${MAX_NOTIFICATIONS + 5}`);
  });

  it('starts empty for another user', () => {
    events.next(event('ticketCreated'));
    user.set({ id: 'u2' });
    TestBed.tick();
    expect(state.items()).toEqual([]);
    expect(state.announcement()).toBe('');
  });
});
