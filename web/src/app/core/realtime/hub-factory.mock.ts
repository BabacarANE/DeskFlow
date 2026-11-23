import { HubFactory, HubLike } from './hub';
import { HubMethod } from './realtime-events';
import { mockTicketRefs } from '../mock/mock-tickets';

/**
 * Fake hub of the `mock` build: no network, payloads follow docs/api/events.md section 5.
 * Quiet by default. To see notifications, run in the browser console:
 *   deskflowMockRealtime.start(10)      // one event every 10 s
 *   deskflowMockRealtime.emit('ticketReplied')
 *   deskflowMockRealtime.stop()
 * or `sessionStorage.setItem('deskflow.mock.realtime', '15'); location.reload();`
 */
const STORAGE_KEY = 'deskflow.mock.realtime';
const KINDS: HubMethod[] = ['ticketCreated', 'ticketAssigned', 'ticketReplied'];
const PRIORITIES = ['Low', 'Normal', 'High', 'Urgent'];

const hubs = new Set<FakeHub>();
let timer: ReturnType<typeof setInterval> | undefined;
let cursor = 0;

class FakeHub implements HubLike {
  private readonly handlers = new Map<string, (payload: unknown) => void>();

  start(): Promise<void> {
    hubs.add(this);
    return Promise.resolve();
  }

  stop(): Promise<void> {
    hubs.delete(this);
    return Promise.resolve();
  }

  on(method: string, handler: (payload: unknown) => void): void {
    this.handlers.set(method, handler);
  }

  onclose(): void {
    // The fake connection never drops.
  }

  deliver(method: HubMethod, payload: unknown): void {
    this.handlers.get(method)?.(payload);
  }
}

function emit(kind: HubMethod = KINDS[cursor % KINDS.length]): void {
  const tickets = mockTicketRefs();
  const ticket = tickets[cursor++ % tickets.length];
  if (!ticket) return;
  const payload = {
    ticketId: ticket.id,
    number: ticket.number,
    title: ticket.title,
    at: new Date().toISOString(),
    ...(kind === 'ticketCreated' ? { priority: PRIORITIES[cursor % PRIORITIES.length] } : {}),
  };
  hubs.forEach((hub) => hub.deliver(kind, payload));
}

function start(seconds = 15): void {
  stop();
  timer = setInterval(() => emit(), Math.max(1, seconds) * 1000);
}

function stop(): void {
  clearInterval(timer);
  timer = undefined;
}

declare global {
  interface Window {
    deskflowMockRealtime?: { start: typeof start; stop: typeof stop; emit: typeof emit };
  }
}

export const createHub: HubFactory = () => {
  if (typeof window !== 'undefined' && !window.deskflowMockRealtime) {
    window.deskflowMockRealtime = { start, stop, emit };
    const configured = sessionStorage.getItem(STORAGE_KEY);
    if (configured) start(Number(configured) || 15);
  }
  return new FakeHub();
};
