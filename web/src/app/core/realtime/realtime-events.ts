import { TicketPriority } from '../api/models';

/** Server to client methods of the hub (docs/api/events.md, section 5). */
export const HUB_URL = '/hubs/notifications';
export const HUB_METHODS = ['ticketCreated', 'ticketAssigned', 'ticketReplied'] as const;
export type HubMethod = (typeof HUB_METHODS)[number];

interface Base {
  ticketId: string;
  number: number;
  title: string;
  at: string;
}

export type RealtimeEvent =
  | ({ kind: 'ticketCreated'; priority: TicketPriority } & Base)
  | ({ kind: 'ticketAssigned' } & Base)
  | ({ kind: 'ticketReplied' } & Base);

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const PRIORITY_VALUES: readonly string[] = ['Low', 'Normal', 'High', 'Urgent'];
const MAX_TITLE_LENGTH = 300;

/** Validates a hub payload strictly; anything that does not match the contract is dropped silently. */
export function parseEvent(kind: HubMethod, payload: unknown): RealtimeEvent | null {
  if (typeof payload !== 'object' || payload === null) return null;
  const p = payload as Record<string, unknown>;
  const { ticketId, number, title, at } = p;
  if (
    typeof ticketId !== 'string' ||
    !UUID.test(ticketId) ||
    typeof number !== 'number' ||
    !Number.isSafeInteger(number) ||
    number < 1 ||
    typeof title !== 'string' ||
    title.length > MAX_TITLE_LENGTH ||
    typeof at !== 'string' ||
    Number.isNaN(Date.parse(at))
  ) {
    return null;
  }
  const base: Base = { ticketId, number, title, at };
  if (kind === 'ticketCreated') {
    const priority = p['priority'];
    return typeof priority === 'string' && PRIORITY_VALUES.includes(priority)
      ? { kind, ...base, priority: priority as TicketPriority }
      : null;
  }
  return { kind, ...base };
}
