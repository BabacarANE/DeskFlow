import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
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
  failStart: unknown = false;
  constructor(readonly accessToken: () => string) {}
  start(): Promise<void> {
    this.started++;
    return this.failStart
      ? Promise.reject(this.failStart === true ? new Error('down') : this.failStart)
      : Promise.resolve();
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
  let failNext: unknown;
  const refreshToken = vi.fn();
  const clear = vi.fn();
  let service: RealtimeService;

  /** A JWT-shaped token expiring in `seconds` from now. */
  const jwt = (seconds: number) =>
    `h.${btoa(JSON.stringify({ exp: Math.floor(Date.now() / 1000) + seconds }))}.s`;

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
    clear.mockReset();
    refreshToken.mockImplementation(() => {
      token = 'token-2';
      return of('token-2');
    });
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: AuthService,
          useValue: {
            user,
            refreshToken,
            clear,
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
    const base = { ticketId: '40000000-0000-4000-8000-000000000001', number: 12, title: 'VPN', at };
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

  it('goes offline quietly, then reconnects after a backoff without renewing a valid token', async () => {
    setup();
    token = jwt(900);
    failNext = true;
    user.set(me('Agent'));
    await flush();
    expect(service.status()).toBe('offline');
    failNext = false;
    await vi.advanceTimersByTimeAsync(backoffDelay(0, 1));
    expect(refreshToken).not.toHaveBeenCalled();
    expect(hubs).toHaveLength(2);
    expect(service.status()).toBe('connected');
  });

  it('renews a token close to its expiry exactly once before reconnecting', async () => {
    setup();
    token = jwt(30);
    failNext = true;
    user.set(me('Agent'));
    await flush();
    failNext = false;
    await vi.advanceTimersByTimeAsync(backoffDelay(0, 1));
    expect(refreshToken).toHaveBeenCalledTimes(1);
    expect(hubs[1].accessToken()).toBe('token-2');
    expect(service.status()).toBe('connected');
  });

  it('renews the token after the hub answered 401', async () => {
    setup();
    token = jwt(900);
    failNext = { statusCode: 401 };
    user.set(me('Agent'));
    await flush();
    failNext = false;
    await vi.advanceTimersByTimeAsync(backoffDelay(0, 1));
    expect(refreshToken).toHaveBeenCalledTimes(1);
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

  it('keeps retrying when the renewal fails because the server is unreachable', async () => {
    setup();
    token = jwt(10);
    refreshToken.mockImplementation(() => throwError(() => ({ status: 0 })));
    failNext = true;
    user.set(me('Agent'));
    await flush();
    await vi.advanceTimersByTimeAsync(backoffDelay(0, 1));
    expect(hubs).toHaveLength(2);
    expect(clear).not.toHaveBeenCalled();
  });

  it('stops the loop, clears the session and goes to sign-in when the renewal answers 401', async () => {
    setup();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    token = jwt(10);
    refreshToken.mockImplementation(() => throwError(() => ({ status: 401 })));
    failNext = true;
    user.set(me('Agent'));
    await flush();
    await vi.advanceTimersByTimeAsync(backoffDelay(0, 1));
    expect(clear).toHaveBeenCalledTimes(1);
    expect(navigate).toHaveBeenCalledWith(['/login']);
    await vi.advanceTimersByTimeAsync(120_000);
    expect(hubs).toHaveLength(1);
    expect(refreshToken).toHaveBeenCalledTimes(1);
  });

  it('stops retrying after sign-out', async () => {
    setup();
    failNext = true;
    user.set(me('Agent'));
    await flush();
    user.set(null);
    await flush();
    await vi.advanceTimersByTimeAsync(60_000);
    expect(hubs).toHaveLength(1);
  });

  it('caps the backoff at 30 seconds (plus jitter)', () => {
    expect(backoffDelay(0, 0)).toBe(1000);
    expect(backoffDelay(3, 0)).toBe(8000);
    expect(backoffDelay(20, 1)).toBe(36_000);
  });
});
