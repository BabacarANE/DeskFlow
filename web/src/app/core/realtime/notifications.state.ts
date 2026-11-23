import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { AuthService } from '../auth/auth.service';
import { RealtimeEvent } from './realtime-events';
import { RealtimeService } from './realtime.service';

export const MAX_NOTIFICATIONS = 50;

export interface AppNotification {
  id: number;
  kind: RealtimeEvent['kind'];
  ticketId: string;
  text: string;
  at: string;
  read: boolean;
}

export function notificationText(event: RealtimeEvent): string {
  switch (event.kind) {
    case 'ticketCreated':
      return `Nouveau ticket #${event.number} : ${event.title}`;
    case 'ticketAssigned':
      return `Ticket #${event.number} vous a été assigné`;
    case 'ticketReplied':
      return `Le demandeur a répondu sur #${event.number} : ${event.title}`;
  }
}

/** Recent notifications of the session: in memory only, newest first, capped. */
@Injectable({ providedIn: 'root' })
export class NotificationsState {
  private readonly realtime = inject(RealtimeService);
  private readonly auth = inject(AuthService);
  private readonly userId = computed(() => this.auth.user()?.id ?? null);
  private nextId = 1;
  private spoken = false;

  readonly items = signal<AppNotification[]>([]);
  readonly unreadCount = computed(() => this.items().filter((n) => !n.read).length);
  /** Text for the polite live region; replaced at each arrival. */
  readonly announcement = signal('');

  constructor() {
    this.realtime.events.pipe(takeUntilDestroyed()).subscribe((event) => this.add(event));
    // A different user (or a sign-out) never inherits the previous list.
    effect(() => {
      this.userId();
      untracked(() => {
        this.items.set([]);
        this.announcement.set('');
      });
    });
  }

  add(event: RealtimeEvent): void {
    const text = notificationText(event);
    const notification: AppNotification = {
      id: this.nextId++,
      kind: event.kind,
      ticketId: event.ticketId,
      text,
      at: event.at,
      read: false,
    };
    this.items.update((list) => [notification, ...list].slice(0, MAX_NOTIFICATIONS));
    // Two identical texts in a row would not change the live region: alternate an invisible suffix.
    this.spoken = !this.spoken;
    this.announcement.set(`Nouvelle notification : ${text}${this.spoken ? '' : ' '}`);
  }

  markRead(id: number): void {
    this.items.update((list) => list.map((n) => (n.id === id ? { ...n, read: true } : n)));
  }

  markAllRead(): void {
    this.items.update((list) => list.map((n) => (n.read ? n : { ...n, read: true })));
  }
}
