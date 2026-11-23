import { Injectable, Injector, computed, effect, inject, signal, untracked } from '@angular/core';
import { Subject, firstValueFrom } from 'rxjs';
import { isAgentRole } from '../api/ticket-rules';
import { AuthService } from '../auth/auth.service';
import { HUB_FACTORY, HubLike } from './hub';
import { HUB_METHODS, RealtimeEvent, parseEvent } from './realtime-events';

export type RealtimeStatus = 'idle' | 'connecting' | 'connected' | 'offline';

const MAX_DELAY_MS = 30_000;
const BASE_DELAY_MS = 1_000;

/** Exponential backoff (1 s, 2 s, 4 s ... 30 s) with up to 20 % jitter. */
export function backoffDelay(retry: number, random: number = Math.random()): number {
  const base = Math.min(MAX_DELAY_MS, BASE_DELAY_MS * 2 ** retry);
  return Math.round(base * (1 + 0.2 * random));
}

/**
 * Real-time channel (hub /hubs/notifications, docs/api/events.md section 5). Only Agent and
 * TenantAdmin connect: a Requester has no event in this phase and a SuperAdmin has no tenant.
 * The access token comes from memory at each attempt and is renewed before each retry.
 */
@Injectable({ providedIn: 'root' })
export class RealtimeService {
  private readonly auth = inject(AuthService);
  private readonly injector = inject(Injector);
  private readonly hubFactory = inject(HUB_FACTORY);
  private readonly subject = new Subject<RealtimeEvent>();

  private hub?: HubLike;
  private generation = 0;
  private retries = 0;
  private timer?: ReturnType<typeof setTimeout>;
  private started = false;

  readonly events = this.subject.asObservable();
  readonly status = signal<RealtimeStatus>('idle');
  /** Id of the user the connection belongs to, null when no connection is wanted. */
  private readonly userKey = computed(() => {
    const user = this.auth.user();
    return user && isAgentRole(user.role) ? user.id : null;
  });

  /** Starts following the session (called once by the root component). */
  init(): void {
    if (this.started) return;
    this.started = true;
    effect(
      () => {
        const key = this.userKey();
        untracked(() => (key ? this.connect() : this.disconnect()));
      },
      { injector: this.injector },
    );
  }

  private connect(): void {
    this.disconnect();
    const generation = this.generation;
    this.status.set('connecting');
    void this.attempt(generation);
  }

  private disconnect(): void {
    this.generation++;
    clearTimeout(this.timer);
    this.timer = undefined;
    this.retries = 0;
    const hub = this.hub;
    this.hub = undefined;
    if (hub) hub.stop().catch(() => undefined);
    this.status.set('idle');
  }

  private async attempt(generation: number): Promise<void> {
    if (generation !== this.generation) return;
    let hub: HubLike;
    try {
      hub = this.hubFactory(() => this.auth.accessToken ?? '');
    } catch {
      this.scheduleRetry(generation); // the connection cannot even be built: stay offline
      return;
    }
    this.hub = hub;
    for (const method of HUB_METHODS) {
      hub.on(method, (payload) => {
        const event = parseEvent(method, payload);
        if (event && generation === this.generation) this.subject.next(event);
      });
    }
    hub.onclose(() => {
      if (generation === this.generation && this.hub === hub) this.scheduleRetry(generation);
    });
    try {
      await hub.start();
    } catch {
      // Offline is a quiet state: the REST API stays the source of truth.
      if (generation === this.generation) this.scheduleRetry(generation);
      return;
    }
    if (generation !== this.generation) {
      hub.stop().catch(() => undefined);
      return;
    }
    this.retries = 0;
    this.status.set('connected');
  }

  private scheduleRetry(generation: number): void {
    this.status.set('offline');
    this.hub = undefined;
    clearTimeout(this.timer); // never keep two pending retries
    const delay = backoffDelay(this.retries++);
    this.timer = setTimeout(async () => {
      // The previous token may have expired while offline: renew it (single flight) first.
      try {
        await firstValueFrom(this.auth.refreshToken());
      } catch {
        // Still unreachable or session over: the interceptor and the auth state handle it.
      }
      if (generation === this.generation) {
        this.status.set('connecting');
        void this.attempt(generation);
      }
    }, delay);
  }
}
