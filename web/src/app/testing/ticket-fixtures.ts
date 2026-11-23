import { TicketDetail, TicketSummary } from '../core/api/models';

/** Test data builders for the ticket screens. */
export function summary(id: string, overrides: Partial<TicketSummary> = {}): TicketSummary {
  return {
    id,
    number: Number(id.replace(/\D/g, '')) || 1,
    title: `Ticket ${id}`,
    status: 'Open',
    priority: 'Normal',
    category: null,
    requester: { id: 'req', displayName: 'Dana Demandeur' },
    assignee: null,
    createdAt: '2026-11-01T09:00:00Z',
    updatedAt: '2026-11-01T10:00:00Z',
    responseDueAt: null,
    resolutionDueAt: null,
    escalatedAt: null,
    ...overrides,
  };
}

export function detail(id: string, overrides: Partial<TicketDetail> = {}): TicketDetail {
  return {
    ...summary(id),
    description: 'Le VPN refuse ma connexion.',
    rowVersion: '1',
    comments: [],
    ...overrides,
  };
}
