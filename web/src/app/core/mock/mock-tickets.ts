import {
  Category,
  Comment,
  CreateCommentRequest,
  CreateTicketRequest,
  Me,
  Page,
  TicketDetail,
  TicketEvent,
  TicketEventType,
  TicketPriority,
  TicketStatus,
  TicketSummary,
  UpdateTicketRequest,
  User,
  UserRef,
} from '../api/models';
import { TRANSITIONS, isAgentRole } from '../api/ticket-rules';

/**
 * Ticket part of the simulated backend. It follows the frozen phase 2 contract
 * (docs/api/openapi.json): valid transitions, rowVersion and 409, internal comments hidden from
 * the Requester, filters, sort and pagination. Pure logic: it returns a MockResult and knows
 * nothing about HTTP, so the interceptor stays the only place that builds responses.
 */

export interface MockResult {
  status: number;
  body?: unknown;
  /** Present for failures: becomes a ProblemDetails body. */
  problem?: { title: string; type?: string; errors?: Record<string, string[]> };
}

export interface MockContext {
  me: Me;
  users: User[];
  method: string;
  path: string;
  query: URLSearchParams;
  body: unknown;
}

const PROBLEM_BASE = 'https://deskflow.example/problems/';

/** SLA minutes per priority (SPEC section 6): first response, resolution. */
const SLA_MINUTES: Record<TicketPriority, [number, number]> = {
  Urgent: [30, 240],
  High: [120, 480],
  Normal: [480, 1440],
  Low: [1440, 4320],
};

const PRIORITY_RANK: Record<TicketPriority, number> = { Low: 0, Normal: 1, High: 2, Urgent: 3 };
const PRIORITIES = Object.keys(PRIORITY_RANK) as TicketPriority[];
const STATUSES = Object.keys(TRANSITIONS) as TicketStatus[];

interface MockTicket {
  id: string;
  number: number;
  title: string;
  description: string;
  status: TicketStatus;
  priority: TicketPriority;
  category: Category | null;
  requester: UserRef;
  assignee: UserRef | null;
  createdAt: string;
  updatedAt: string;
  responseDueAt: string | null;
  resolutionDueAt: string | null;
  escalatedAt: string | null;
  firstResponseAt: string | null;
  resolvedAt: string | null;
  pendingSince: string | null;
  version: number;
  comments: Comment[];
  events: TicketEvent[];
}

interface TicketState {
  categories: Category[];
  tickets: MockTicket[];
  nextId: number;
}

const U = {
  agent: { id: '10000000-0000-4000-8000-000000000002', displayName: 'Adam Agent' },
  lea: { id: '10000000-0000-4000-8000-000000000004', displayName: 'Léa Martin' },
  requester: { id: '10000000-0000-4000-8000-000000000003', displayName: 'Dana Demandeur' },
  other: { id: '10000000-0000-4000-8000-000000000005', displayName: 'Omar Collègue' },
};

const MINUTE = 60_000;

function problem(status: number, title: string, type?: string, errors?: Record<string, string[]>) {
  return { status, problem: { title, ...(type ? { type: PROBLEM_BASE + type } : {}), errors } };
}

function addMinutes(iso: string, minutes: number): string {
  return new Date(new Date(iso).getTime() + minutes * MINUTE).toISOString();
}

function dueDates(createdAt: string, priority: TicketPriority, shiftMinutes = 0) {
  const [first, resolution] = SLA_MINUTES[priority];
  return {
    responseDueAt: addMinutes(createdAt, first + shiftMinutes),
    resolutionDueAt: addMinutes(createdAt, resolution + shiftMinutes),
  };
}

function seed(): TicketState {
  const categories: Category[] = [
    { id: '30000000-0000-4000-8000-000000000001', name: 'Accès et comptes' },
    { id: '30000000-0000-4000-8000-000000000002', name: 'Matériel' },
    { id: '30000000-0000-4000-8000-000000000003', name: 'Logiciels' },
  ];
  const state: TicketState = { categories, tickets: [], nextId: 100 };
  const now = Date.now();
  const titles = [
    'Impossible de me connecter au VPN',
    'Écran qui scintille',
    'Demande d’installation de Figma',
    'Mot de passe oublié',
    'Imprimante du 2e étage hors service',
    'Accès au dossier partagé Finance',
    'Souris sans fil défectueuse',
    'Mise à jour du système bloquée',
    'Nouvelle adresse e-mail pour un stagiaire',
    'Wi-Fi instable en salle de réunion',
    'Licence Office expirée',
    'Casque audio qui grésille',
  ];
  const statuses: TicketStatus[] = [
    'New',
    'Open',
    'Open',
    'Pending',
    'Resolved',
    'Closed',
    'New',
    'Open',
  ];
  const priorities: TicketPriority[] = ['Normal', 'High', 'Low', 'Urgent'];
  for (let i = 0; i < 26; i++) {
    const status = statuses[i % statuses.length];
    const priority = priorities[i % priorities.length];
    const requester = i % 3 === 0 ? U.other : U.requester;
    const createdAt = new Date(now - (i * 5 + 1) * 3_600_000).toISOString();
    const assignee = status === 'New' ? null : i % 2 === 0 ? U.agent : U.lea;
    const ticket: MockTicket = {
      id: `40000000-0000-4000-8000-${String(i + 1).padStart(12, '0')}`,
      number: i + 1,
      title: `${titles[i % titles.length]}${i >= titles.length ? ` (${i + 1})` : ''}`,
      description: 'Bonjour,\nle problème dure depuis ce matin et bloque mon travail.\nMerci.',
      status,
      priority,
      category: i % 4 === 3 ? null : categories[i % categories.length],
      requester,
      assignee,
      createdAt,
      updatedAt: addMinutes(createdAt, 20),
      ...dueDates(createdAt, priority),
      escalatedAt: null,
      firstResponseAt: null,
      resolvedAt: status === 'Resolved' || status === 'Closed' ? addMinutes(createdAt, 90) : null,
      pendingSince: status === 'Pending' ? addMinutes(createdAt, 40) : null,
      version: 1,
      comments: [],
      events: [
        { id: eventId(state), type: 'Created', actor: requester, at: createdAt, newValue: null },
      ],
    };
    if (status !== 'New') {
      ticket.firstResponseAt = addMinutes(createdAt, 15);
      ticket.comments.push(
        {
          id: commentId(state),
          author: requester,
          body: 'Voici plus de détails : le message d’erreur s’affiche à chaque tentative.',
          isInternal: false,
          createdAt: addMinutes(createdAt, 5),
        },
        {
          id: commentId(state),
          author: assignee ?? U.agent,
          body: 'Bien reçu, je regarde cela tout de suite.',
          isInternal: false,
          createdAt: ticket.firstResponseAt,
        },
        {
          id: commentId(state),
          author: assignee ?? U.agent,
          body: 'Note interne : même symptôme que le ticket précédent, voir la procédure.',
          isInternal: true,
          createdAt: addMinutes(createdAt, 18),
        },
      );
      ticket.events.push(
        {
          id: eventId(state),
          type: 'AssigneeChanged',
          actor: U.agent,
          oldValue: null,
          newValue: assignee?.displayName ?? null,
          at: addMinutes(createdAt, 10),
        },
        {
          id: eventId(state),
          type: 'StatusChanged',
          actor: U.agent,
          oldValue: 'New',
          newValue: 'Open',
          at: addMinutes(createdAt, 12),
        },
      );
    }
    state.tickets.push(ticket);
  }
  return state;
}

function eventId(state: TicketState): string {
  return `50000000-0000-4000-8000-${String(state.nextId++).padStart(12, '0')}`;
}

function commentId(state: TicketState): string {
  return `60000000-0000-4000-8000-${String(state.nextId++).padStart(12, '0')}`;
}

let state = seed();

/** Test helper: restores the initial ticket data set. */
export function resetMockTickets(): void {
  state = seed();
}

function toSummary(t: MockTicket): TicketSummary {
  return {
    id: t.id,
    number: t.number,
    title: t.title,
    status: t.status,
    priority: t.priority,
    category: t.category,
    requester: t.requester,
    assignee: t.assignee,
    createdAt: t.createdAt,
    updatedAt: t.updatedAt,
    responseDueAt: t.responseDueAt,
    resolutionDueAt: t.resolutionDueAt,
    escalatedAt: t.escalatedAt,
  };
}

function toDetail(t: MockTicket, me: Me): TicketDetail {
  const agent = isAgentRole(me.role);
  return {
    ...toSummary(t),
    description: t.description,
    rowVersion: String(t.version),
    firstResponseAt: t.firstResponseAt,
    resolvedAt: t.resolvedAt,
    comments: t.comments.filter((c) => agent || !c.isInternal).map((c) => ({ ...c })),
  };
}

function visibleTo(t: MockTicket, me: Me): boolean {
  return isAgentRole(me.role) || t.requester.id === me.id;
}

function addEvent(
  t: MockTicket,
  type: TicketEventType,
  actor: UserRef,
  oldValue: string | null,
  newValue: string | null,
  at: string,
): void {
  t.events.push({ id: eventId(state), type, actor, oldValue, newValue, at });
}

function sanitise(text: string): string {
  return text.trim();
}

function listTickets(ctx: MockContext): MockResult {
  const { query, me } = ctx;
  const agent = isAgentRole(me.role);
  const page = Number(query.get('page') ?? 1);
  const pageSize = Number(query.get('pageSize') ?? 20);
  const sort = query.get('sort') ?? 'createdAt';
  const direction = query.get('direction') ?? 'desc';
  const status = query.get('status');
  const priority = query.get('priority');
  const errors: Record<string, string[]> = {};
  if (!Number.isInteger(page) || page < 1) errors['page'] = ['La page doit être au moins 1.'];
  if (!Number.isInteger(pageSize) || pageSize < 1 || pageSize > 100) {
    errors['pageSize'] = ['La taille de page doit être comprise entre 1 et 100.'];
  }
  if (status && !STATUSES.includes(status as TicketStatus)) errors['status'] = ['Statut inconnu.'];
  if (priority && !PRIORITIES.includes(priority as TicketPriority)) {
    errors['priority'] = ['Priorité inconnue.'];
  }
  if (!['createdAt', 'updatedAt', 'priority', 'number'].includes(sort)) {
    errors['sort'] = ['Tri inconnu.'];
  }
  if (!['asc', 'desc'].includes(direction)) errors['direction'] = ['Sens de tri inconnu.'];
  const q = (query.get('q') ?? '').trim().toLowerCase();
  if (q.length > 100) errors['q'] = ['La recherche ne peut pas dépasser 100 caractères.'];
  if (Object.keys(errors).length > 0) return problem(422, 'Validation failed', undefined, errors);

  const assigneeId = query.get('assigneeId');
  const unassigned = query.get('unassigned') === 'true';
  const categoryId = query.get('categoryId');
  let items = state.tickets.filter((t) => visibleTo(t, me));
  if (status) items = items.filter((t) => t.status === status);
  if (priority) items = items.filter((t) => t.priority === priority);
  if (categoryId) items = items.filter((t) => t.category?.id === categoryId);
  // The assignee filters are ignored for a Requester; `unassigned` wins over `assigneeId`.
  if (agent && unassigned) items = items.filter((t) => t.assignee === null);
  else if (agent && assigneeId) items = items.filter((t) => t.assignee?.id === assigneeId);
  if (q) {
    items = items.filter((t) => t.title.toLowerCase().includes(q) || String(t.number) === q);
  }

  const sign = direction === 'asc' ? 1 : -1;
  const key = (t: MockTicket): number | string =>
    sort === 'priority'
      ? PRIORITY_RANK[t.priority]
      : sort === 'number'
        ? t.number
        : sort === 'updatedAt'
          ? t.updatedAt
          : t.createdAt;
  items = [...items].sort((a, b) => {
    const ka = key(a);
    const kb = key(b);
    const cmp = ka < kb ? -1 : ka > kb ? 1 : 0;
    return (cmp || a.number - b.number) * sign;
  });

  const result: Page<TicketSummary> = {
    items: items.slice((page - 1) * pageSize, page * pageSize).map(toSummary),
    total: items.length,
    page,
    pageSize,
  };
  return { status: 200, body: result };
}

function createTicket(ctx: MockContext): MockResult {
  const req = (ctx.body ?? {}) as Partial<CreateTicketRequest>;
  const errors: Record<string, string[]> = {};
  const title = sanitise(req.title ?? '');
  const description = sanitise(req.description ?? '');
  if (!title) errors['title'] = ['Le titre est obligatoire.'];
  else if (title.length > 200) errors['title'] = ['Le titre ne peut pas dépasser 200 caractères.'];
  if (!description) errors['description'] = ['La description est obligatoire.'];
  else if (description.length > 5000) {
    errors['description'] = ['La description ne peut pas dépasser 5000 caractères.'];
  }
  const category = req.categoryId ? state.categories.find((c) => c.id === req.categoryId) : null;
  if (req.categoryId && !category) errors['categoryId'] = ['Catégorie inconnue.'];
  const priority = req.priority ?? 'Normal';
  if (!PRIORITIES.includes(priority)) errors['priority'] = ['Priorité inconnue.'];
  if (Object.keys(errors).length > 0) return problem(422, 'Validation failed', undefined, errors);

  const createdAt = new Date().toISOString();
  const requester = { id: ctx.me.id, displayName: ctx.me.displayName };
  const ticket: MockTicket = {
    id: `40000000-0000-4000-8000-${String(state.nextId++).padStart(12, '0')}`,
    number: Math.max(0, ...state.tickets.map((t) => t.number)) + 1,
    title,
    description,
    status: 'New',
    priority,
    category: category ?? null,
    requester,
    assignee: null,
    createdAt,
    updatedAt: createdAt,
    ...dueDates(createdAt, priority),
    escalatedAt: null,
    firstResponseAt: null,
    resolvedAt: null,
    pendingSince: null,
    version: 1,
    comments: [],
    events: [],
  };
  addEvent(ticket, 'Created', requester, null, null, createdAt);
  state.tickets.unshift(ticket);
  return { status: 201, body: toDetail(ticket, ctx.me) };
}

function updateTicket(ctx: MockContext, t: MockTicket): MockResult {
  if (!isAgentRole(ctx.me.role)) return problem(403, 'Forbidden');
  const req = (ctx.body ?? {}) as Partial<UpdateTicketRequest>;
  if (!req.rowVersion) {
    return problem(422, 'Validation failed', undefined, {
      rowVersion: ['La version du ticket est obligatoire.'],
    });
  }
  if (req.rowVersion !== String(t.version)) {
    return problem(409, 'Le ticket a été modifié par quelqu’un d’autre.', 'concurrency-conflict');
  }

  const errors: Record<string, string[]> = {};
  if (req.priority !== undefined && !PRIORITIES.includes(req.priority)) {
    errors['priority'] = ['Priorité inconnue.'];
  }
  if (req.status !== undefined && !STATUSES.includes(req.status)) {
    errors['status'] = ['Statut inconnu.'];
  }
  let assignee: UserRef | null | undefined;
  if (req.assigneeId !== undefined) {
    if (req.assigneeId === null) assignee = null;
    else {
      const user = ctx.users.find(
        (u) => u.id === req.assigneeId && u.isActive && isAgentRole(u.role),
      );
      if (user) assignee = { id: user.id, displayName: user.displayName };
      else errors['assigneeId'] = ['Cet utilisateur ne peut pas être assigné.'];
    }
  }
  let category: Category | null | undefined;
  if (req.categoryId !== undefined) {
    if (req.categoryId === null) category = null;
    else {
      category = state.categories.find((c) => c.id === req.categoryId);
      if (!category) errors['categoryId'] = ['Catégorie inconnue.'];
    }
  }
  if (Object.keys(errors).length > 0) return problem(422, 'Validation failed', undefined, errors);

  if (req.status !== undefined && req.status !== t.status) {
    if (!TRANSITIONS[t.status].includes(req.status)) {
      return problem(
        409,
        `La transition de ${t.status} vers ${req.status} n’est pas autorisée.`,
        'invalid-transition',
      );
    }
  }

  const now = new Date();
  const at = now.toISOString();
  const actor = { id: ctx.me.id, displayName: ctx.me.displayName };
  let changed = false;

  if (req.status !== undefined && req.status !== t.status) {
    const from = t.status;
    if (from === 'Pending' && t.pendingSince) {
      // Leaving Pending moves the deadlines by the time spent waiting.
      const spent = Math.round((now.getTime() - new Date(t.pendingSince).getTime()) / MINUTE);
      if (t.responseDueAt) t.responseDueAt = addMinutes(t.responseDueAt, spent);
      if (t.resolutionDueAt) t.resolutionDueAt = addMinutes(t.resolutionDueAt, spent);
      t.pendingSince = null;
    }
    t.status = req.status;
    if (req.status === 'Pending') t.pendingSince = at;
    if (req.status === 'Resolved') t.resolvedAt = at;
    addEvent(t, 'StatusChanged', actor, from, req.status, at);
    changed = true;
  }
  if (req.priority !== undefined && req.priority !== t.priority) {
    const from = t.priority;
    t.priority = req.priority;
    Object.assign(t, dueDates(t.createdAt, req.priority));
    addEvent(t, 'PriorityChanged', actor, from, req.priority, at);
    changed = true;
  }
  if (assignee !== undefined && (assignee?.id ?? null) !== (t.assignee?.id ?? null)) {
    addEvent(
      t,
      'AssigneeChanged',
      actor,
      t.assignee?.displayName ?? null,
      assignee?.displayName ?? null,
      at,
    );
    t.assignee = assignee;
    changed = true;
  }
  if (category !== undefined && (category?.id ?? null) !== (t.category?.id ?? null)) {
    addEvent(t, 'CategoryChanged', actor, t.category?.name ?? null, category?.name ?? null, at);
    t.category = category;
    changed = true;
  }
  if (changed) {
    t.version++;
    t.updatedAt = at;
  }
  return { status: 200, body: toDetail(t, ctx.me) };
}

function addComment(ctx: MockContext, t: MockTicket): MockResult {
  const req = (ctx.body ?? {}) as Partial<CreateCommentRequest>;
  const agent = isAgentRole(ctx.me.role);
  const body = sanitise(req.body ?? '');
  const isInternal = req.isInternal === true;
  if (isInternal && !agent) return problem(403, 'Forbidden');
  if (!body) {
    return problem(422, 'Validation failed', undefined, { body: ['Le message est obligatoire.'] });
  }
  if (body.length > 5000) {
    return problem(422, 'Validation failed', undefined, {
      body: ['Le message ne peut pas dépasser 5000 caractères.'],
    });
  }
  if (t.status === 'Closed') {
    return problem(409, 'Le ticket est fermé : on ne peut plus y répondre.', 'ticket-closed');
  }

  const at = new Date().toISOString();
  const author = { id: ctx.me.id, displayName: ctx.me.displayName };
  const comment: Comment = { id: commentId(state), author, body, isInternal, createdAt: at };
  t.comments.push(comment);
  if (agent && !isInternal && !t.firstResponseAt) t.firstResponseAt = at;
  if (!agent && t.status === 'Pending') {
    // A requester answering a Pending ticket reopens it.
    if (t.pendingSince) {
      const spent = Math.round((Date.now() - new Date(t.pendingSince).getTime()) / MINUTE);
      if (t.responseDueAt) t.responseDueAt = addMinutes(t.responseDueAt, spent);
      if (t.resolutionDueAt) t.resolutionDueAt = addMinutes(t.resolutionDueAt, spent);
      t.pendingSince = null;
    }
    t.status = 'Open';
    addEvent(t, 'StatusChanged', author, 'Pending', 'Open', at);
    t.version++;
  }
  t.updatedAt = at;
  return { status: 201, body: comment };
}

/** Returns null when the path is not a ticket route (the caller keeps routing). */
export function handleTicketRoutes(ctx: MockContext): MockResult | null {
  const { method, path, me } = ctx;

  if (path === '/categories' && method === 'GET') {
    return {
      status: 200,
      body: [...state.categories].sort((a, b) => a.name.localeCompare(b.name)),
    };
  }

  if (path === '/assignees' || path === '/tickets' || path.startsWith('/tickets/')) {
    if (me.role === 'SuperAdmin') return problem(403, 'Forbidden');
  }

  if (path === '/assignees' && method === 'GET') {
    if (!isAgentRole(me.role)) return problem(403, 'Forbidden');
    const refs = ctx.users
      .filter((u) => u.isActive && isAgentRole(u.role))
      .map((u) => ({ id: u.id, displayName: u.displayName }))
      .sort((a, b) => a.displayName.localeCompare(b.displayName));
    return { status: 200, body: refs };
  }

  if (path === '/tickets') {
    if (method === 'GET') return listTickets(ctx);
    if (method === 'POST') return createTicket(ctx);
    return null;
  }

  const match = /^\/tickets\/([^/]+)(?:\/(comments|events))?$/.exec(path);
  if (!match) return null;
  const ticket = state.tickets.find((t) => t.id === match[1]);
  // Unknown, foreign and other tenants' tickets all answer 404.
  if (!ticket || !visibleTo(ticket, me)) return problem(404, 'Not found');

  const sub = match[2];
  if (!sub && method === 'GET') return { status: 200, body: toDetail(ticket, me) };
  if (!sub && method === 'PATCH') return updateTicket(ctx, ticket);
  if (sub === 'comments' && method === 'POST') return addComment(ctx, ticket);
  if (sub === 'events' && method === 'GET') {
    return { status: 200, body: ticket.events.map((e) => ({ ...e })) };
  }
  return null;
}
