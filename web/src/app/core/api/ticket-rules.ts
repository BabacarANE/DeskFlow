import { Role, TicketStatus, TicketSummary } from './models';

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
 * True while the SLA clock runs (New or Open) and a deadline is behind us.
 * The response deadline only counts while the ticket is still New (no agent has taken it).
 */
export function isSlaBreached(ticket: TicketSummary, now: Date = new Date()): boolean {
  if (ticket.status !== 'New' && ticket.status !== 'Open') return false;
  const deadlines = [
    ticket.resolutionDueAt ?? null,
    ticket.status === 'New' ? (ticket.responseDueAt ?? null) : null,
  ];
  return deadlines.some((d) => d !== null && new Date(d).getTime() < now.getTime());
}
