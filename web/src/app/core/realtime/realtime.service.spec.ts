import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { Me, Role } from '../api/models';
import { AuthService } from '../auth/auth.service';
import { HUB_FACTORY, HubLike } from './hub';
import { RealtimeEvent } from './realtime-events';
import { RealtimeService, backoffDelay } from './realtime.service';

class FakeHub implements HubLike {
  handlers = new Map<string, (payload: unknown) => void>();
  closeHandler?: () => void;
  started = 0;
  stopped = 0;
  failStart = false;
  constructor(readonly accessToken: () => string) {}
  start(): Promise<void> {
    this.started++;
    return this.failStart ? Promise.reject(new Error('down')) : Promise.resolve();
  }
  stop(): Promise<void> {
    this.stopped++;
    return Promise.resolve();
  }
  on(method: string, handler: (payload: unknown) => void): void {
    this.handlers.set(method, handler);
  }
  onclose(handler: () => void): void {
    this.closeHandler = handler;
  }
}

describe('RealtimeService', () => {
  const user = signal<Me | null>(null);
  let token: string | null;
  let hubs: FakeHub[];
  let failNext: boolean;
  const refreshToken = vi.fn();
  let service: RealtimeService;

  const me = (role: Role): Me => ({ id: `u-${role}`, role }) as Me;

  async function flush() {
    TestBed.tick();
    await vi.advanceTimersByTimeAsync(0);
  }

  function setup() {
    hubs = [];
    failNext = false;
    token = 'token-1';
    user.set(null);
    refreshToken.mockReset();
    refreshToken.mockImplementation(() => {
      token = 'token-2';
      return of('token-2');
    });
    TestBed.configureTestingModule({
      providers: [
        {
          provide: AuthService,
          useValue: {
            user,
            refreshToken,
            get accessToken() {
              return token;
            },
          },
        },
        {
          provide: HUB_FACTORY,
          useValue: (accessToken: () => string) => {
            const hub = new FakeHub(accessToken);
            hub.failStart = failNext;
            hubs.push(hub);
            return hub;
          },
        },
      ],
    });
    service = TestBed.inject(RealtimeService);
    service.init();
  }

  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it.each<Role>(['Agent', 'TenantAdmin'])(
    'connects for %s with the in-memory token',
    async (role) => {
      setup();
      user.set(me(role));
      await flush();
      expect(hubs).toHaveLength(1);
      expect(hubs[0].started).toBe(1);
      expect(hubs[0].accessToken()).toBe('token-1');
      expect(service.status()).toBe('connected');
    },
  );

  it.each<Role>(['Requester', 'SuperAdmin'])('never connects for %s', async (role) => {
    setup();
    user.set(me(role));
    await flush();
    expect(hubs).toHaveLength(0);
    expect(service.status()).toBe('idle');
  });

  it('disconnects when the user signs out', async () => {
    setup();
    user.set(me('Agent'));
    await flush();
    user.set(null);
    await flush();
    expect(hubs[0].stopped).toBe(1);
    expect(service.status()).toBe('idle');
  });

  it('emits typed events and drops payloads that break the contract', async () => {
    setup();
    user.set(me('Agent'));
    await flush();
    const received: RealtimeEvent[] = [];
    service.events.subscribe((e) => received.push(e));
    const at = '2026-11-23T10:00:00Z';
    const base = { ticketId: 't1', number: 12, title: 'VPN', at };
    hubs[0].handlers.get('ticketCreated')?.({ ...base, priority: 'High' });
    hubs[0].handlers.get('ticketAssigned')?.(base);
    hubs[0].handlers.get('ticketReplied')?.(base);
    hubs[0].handlers.get('ticketReplied')?.({ number: 12 });
    hubs[0].handlers.get('ticketCreated')?.(base); // priority missing
    expect(received.map((e) => e.kind)).toEqual([
      'ticketCreated',
      'ticketAssigned',
      'ticketReplied',
    ]);
  });

  it('goes offline quietly, then reconnects with a renewed token after a backoff', async () => {
    setup();
    failNext = true;
    user.set(me('Agent'));
    await flush();
    expect(service.status()).toBe('offline');
    failNext = false;
    await vi.advanceTimersByTimeAsync(backoffDelay(0, 1));
    expect(refreshToken).toHaveBeenCalledTimes(1);
    expect(hubs).toHaveLength(2);
    expect(hubs[1].accessToken()).toBe('token-2');
    expect(service.status()).toBe('connected');
  });

  it('reconnects after an unexpected close and lengthens the delay while it keeps failing', async () => {
    setup();
    user.set(me('Agent'));
    await flush();
    hubs[0].closeHandler?.();
    expect(service.status()).toBe('offline');
    failNext = true;
    await vi.advanceTimersByTimeAsync(backoffDelay(0, 1));
    expect(hubs).toHaveLength(2);
    expect(service.status()).toBe('offline');
    await vi.advanceTimersByTimeAsync(backoffDelay(0, 1));
    expect(hubs).toHaveLength(2); // the second delay is longer than the first
    await vi.advanceTimersByTimeAsync(backoffDelay(1, 1));
    expect(hubs).toHaveLength(3);
  });

  it('keeps retrying when the token cannot be renewed, and stops after sign-out', async () => {
    setup();
    refreshToken.mockImplementation(() => throwError(() => new Error('401')));
    failNext = true;
    user.set(me('Agent'));
    await flush();
    await vi.advanceTimersByTimeAsync(backoffDelay(0, 1));
    expect(hubs).toHaveLength(2);
    user.set(null);
    await flush();
    await vi.advanceTimersByTimeAsync(60_000);
    expect(hubs).toHaveLength(2);
  });

  it('caps the backoff at 30 seconds (plus jitter)', () => {
    expect(backoffDelay(0, 0)).toBe(1000);
    expect(backoffDelay(3, 0)).toBe(8000);
    expect(backoffDelay(20, 1)).toBe(36_000);
  });
});
