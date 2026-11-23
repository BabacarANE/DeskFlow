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

/** Validates a hub payload; anything that does not match the contract is dropped. */
export function parseEvent(kind: HubMethod, payload: unknown): RealtimeEvent | null {
  if (typeof payload !== 'object' || payload === null) return null;
  const p = payload as Record<string, unknown>;
  if (
    typeof p['ticketId'] !== 'string' ||
    typeof p['number'] !== 'number' ||
    typeof p['title'] !== 'string' ||
    typeof p['at'] !== 'string'
  ) {
    return null;
  }
  const base: Base = {
    ticketId: p['ticketId'],
    number: p['number'],
    title: p['title'],
    at: p['at'],
  };
  if (kind === 'ticketCreated') {
    return typeof p['priority'] === 'string'
      ? { kind, ...base, priority: p['priority'] as TicketPriority }
      : null;
  }
  return { kind, ...base };
}
