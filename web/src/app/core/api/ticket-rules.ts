import { Role, TicketDetail, TicketStatus, TicketSummary } from './models';

/** Mirror of the contract (SPEC section 6): the only valid status transitions. */
export const TRANSITIONS: Record<TicketStatus, readonly TicketStatus[]> = {
  New: ['Open'],
  Open: ['Pending', 'Resolved'],
  Pending: ['Open'],
  Resolved: ['Open', 'Closed'],
  Closed: [],
};

export function allowedTransitions(status: TicketStatus): readonly TicketStatus[] {
  return TRANSITIONS[status];
}

/** Agents and tenant admins work tickets; requesters only follow their own. */
export function isAgentRole(role: Role | null | undefined): boolean {
  return role === 'Agent' || role === 'TenantAdmin';
}

/**
 * SLA alert of the list. TicketSummary has no firstResponseAt, so the response deadline is never
 * used here: a ticket is late when its resolution deadline passed or it was escalated, while the
 * clock runs (New or Open).
 */
export function isSlaBreached(ticket: TicketSummary, now: Date = new Date()): boolean {
  if (ticket.status !== 'New' && ticket.status !== 'Open') return false;
  if (ticket.escalatedAt) return true;
  return isPast(ticket.resolutionDueAt, now);
}

/** SLA alert of the detail: also late when no agent answered before the response deadline. */
export function isSlaBreachedDetail(ticket: TicketDetail, now: Date = new Date()): boolean {
  if (isSlaBreached(ticket, now)) return true;
  if (ticket.status !== 'New' && ticket.status !== 'Open') return false;
  return !ticket.firstResponseAt && isPast(ticket.responseDueAt, now);
}

function isPast(date: string | null | undefined, now: Date): boolean {
  return !!date && new Date(date).getTime() < now.getTime();
}
