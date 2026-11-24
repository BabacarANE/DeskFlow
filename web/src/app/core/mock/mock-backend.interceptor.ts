import {
  HttpErrorResponse,
  HttpEvent,
  HttpInterceptorFn,
  HttpResponse,
} from '@angular/common/http';
import { Observable, delay, of, throwError } from 'rxjs';
import { API_BASE } from '../api/api-client';
import {
  CreateInvitationRequest,
  CreateTenantRequest,
  DemoRole,
  Invitation,
  Me,
  Page,
  Role,
  Tenant,
  Theme,
  UpdateTenantRequest,
  UpdateUserRequest,
  User,
} from '../api/models';
import { currentMockTheme, handleKbRoutes, resetMockKb } from './mock-kb';
import { MockResult, handleTicketRoutes, resetMockTickets } from './mock-tickets';

/**
 * Simulated backend for developing and testing without the API (`npm run start:mock`).
 * It follows docs/api/openapi.json; data lives in memory and resets on reload.
 * The session survives a reload through sessionStorage (role only, never a token).
 *
 * To try the SuperAdmin screens, run in the browser console:
 *   sessionStorage.setItem('deskflow.mock.role', 'superadmin'); location.reload();
 */

const SESSION_KEY = 'deskflow.mock.role';
let latencyMs = 250;

/** Test helper: the simulated network delay (milliseconds). */
export function setMockLatency(ms: number): void {
  latencyMs = ms;
}

type MockRole = DemoRole | 'superadmin';

interface State {
  tenants: Tenant[];
  users: User[];
  invitations: Invitation[];
  nextId: number;
}

const DEMO_TENANT = {
  id: '00000000-0000-4000-8000-000000000001',
  name: 'Démo SAS',
  slug: 'demo',
  get theme(): Theme {
    return currentMockTheme();
  },
};

const ME_BY_ROLE: Record<MockRole, Me> = {
  admin: {
    id: '10000000-0000-4000-8000-000000000001',
    email: 'admin@demo.example',
    displayName: 'Alice Admin',
    role: 'TenantAdmin',
    tenant: DEMO_TENANT,
    isDemo: true,
  },
  agent: {
    id: '10000000-0000-4000-8000-000000000002',
    email: 'agent@demo.example',
    displayName: 'Adam Agent',
    role: 'Agent',
    tenant: DEMO_TENANT,
    isDemo: true,
  },
  demandeur: {
    id: '10000000-0000-4000-8000-000000000003',
    email: 'demandeur@demo.example',
    displayName: 'Dana Demandeur',
    role: 'Requester',
    tenant: DEMO_TENANT,
    isDemo: true,
  },
  superadmin: {
    id: '10000000-0000-4000-8000-000000000009',
    email: 'root@deskflow.example',
    displayName: 'Sam Super',
    role: 'SuperAdmin',
    tenant: null,
    isDemo: true,
  },
};

function seed(): State {
  const users: User[] = (['admin', 'agent', 'demandeur'] as const).map((key, i) => ({
    id: ME_BY_ROLE[key].id,
    email: ME_BY_ROLE[key].email,
    displayName: ME_BY_ROLE[key].displayName,
    role: ME_BY_ROLE[key].role,
    isActive: true,
    createdAt: `2026-11-0${i + 1}T09:00:00Z`,
  }));
  users.push({
    id: '10000000-0000-4000-8000-000000000004',
    email: 'lea@demo.example',
    displayName: 'Léa Martin',
    role: 'Agent',
    isActive: true,
    createdAt: '2026-11-05T09:00:00Z',
  });
  return {
    tenants: [
      {
        id: DEMO_TENANT.id,
        name: DEMO_TENANT.name,
        slug: DEMO_TENANT.slug,
        isActive: true,
        createdAt: '2026-11-01T08:00:00Z',
      },
      {
        id: '00000000-0000-4000-8000-000000000002',
        name: 'Atelier Nord',
        slug: 'atelier-nord',
        isActive: false,
        createdAt: '2026-11-10T08:00:00Z',
      },
    ],
    users,
    invitations: [
      {
        id: '20000000-0000-4000-8000-000000000001',
        email: 'nouveau@demo.example',
        role: 'Requester',
        expiresAt: '2026-12-01T09:00:00Z',
        acceptedAt: null,
        status: 'Pending',
      },
    ],
    nextId: 100,
  };
}

let state = seed();

/** Test helper: restores the initial data set. */
export function resetMockBackend(): void {
  state = seed();
  resetMockTickets();
  resetMockKb();
}

function problem(status: number, title: string, errors?: Record<string, string[]>, type?: string) {
  return throwError(
    () =>
      new HttpErrorResponse({
        status,
        statusText: title,
        error: { title, status, ...(type ? { type } : {}), ...(errors ? { errors } : {}) },
      }),
  );
}

function fromResult(result: MockResult): Observable<HttpEvent<unknown>> {
  return result.problem
    ? problem(result.status, result.problem.title, result.problem.errors, result.problem.type)
    : ok(result.body ?? null, result.status);
}

function ok<T>(body: T, status = 200): Observable<HttpEvent<unknown>> {
  return of(new HttpResponse<unknown>({ status, body }));
}

function paged<T>(items: T[], query: URLSearchParams): Page<T> {
  const page = Number(query.get('page') ?? 1);
  const pageSize = Number(query.get('pageSize') ?? 20);
  return {
    items: items.slice((page - 1) * pageSize, page * pageSize),
    total: items.length,
    page,
    pageSize,
  };
}

function currentRole(): MockRole | null {
  const stored = sessionStorage.getItem(SESSION_KEY);
  return stored && stored in ME_BY_ROLE ? (stored as MockRole) : null;
}

function guard(required: Role): Observable<HttpEvent<unknown>> | null {
  const role = currentRole();
  if (!role) return problem(401, 'Unauthorized');
  return ME_BY_ROLE[role].role === required ? null : problem(403, 'Forbidden');
}

function handle(
  method: string,
  path: string,
  query: URLSearchParams,
  body: unknown,
): Observable<HttpEvent<unknown>> {
  const token = { accessToken: 'mock-access-token', expiresIn: 900 };

  if (method === 'POST' && path === '/auth/refresh') {
    return currentRole() ? ok(token) : problem(401, 'Unauthorized');
  }
  if (method === 'POST' && path === '/auth/demo') {
    const role = (body as { role?: string } | null)?.role;
    if (role !== 'admin' && role !== 'agent' && role !== 'demandeur') {
      return problem(422, 'Validation failed', { role: ['Rôle de démo inconnu.'] });
    }
    sessionStorage.setItem(SESSION_KEY, role);
    return ok(token);
  }
  if (method === 'POST' && path === '/auth/logout') {
    sessionStorage.removeItem(SESSION_KEY);
    return ok(null, 204);
  }
  if (method === 'GET' && path === '/me') {
    const role = currentRole();
    return role ? ok(ME_BY_ROLE[role]) : problem(401, 'Unauthorized');
  }

  const routeContext = {
    me: ME_BY_ROLE[currentRole() ?? 'demandeur'],
    users: state.users,
    method,
    path,
    query,
    body,
  };
  const ticketResult = handleTicketRoutes(routeContext) ?? handleKbRoutes(routeContext);
  if (ticketResult) {
    return currentRole() ? fromResult(ticketResult) : problem(401, 'Unauthorized');
  }

  if (path === '/users' || path.startsWith('/users/') || path.startsWith('/invitations')) {
    const denied = guard('TenantAdmin');
    if (denied) return denied;
  }
  if (path.startsWith('/tenants')) {
    const denied = guard('SuperAdmin');
    if (denied) return denied;
  }

  if (method === 'GET' && path === '/users') return ok(paged(state.users, query));
  if (method === 'PATCH' && path.startsWith('/users/')) {
    const user = state.users.find((u) => u.id === path.split('/')[2]);
    if (!user) return problem(404, 'Not found');
    const patch = body as UpdateUserRequest;
    if (user.id === ME_BY_ROLE.admin.id) return problem(409, 'Cannot modify yourself');
    if (patch.role) user.role = patch.role;
    if (patch.isActive !== undefined) user.isActive = patch.isActive;
    return ok({ ...user });
  }

  if (method === 'GET' && path === '/invitations') return ok(paged(state.invitations, query));
  if (method === 'POST' && path === '/invitations') {
    const req = body as CreateInvitationRequest;
    const email = (req.email ?? '').trim().toLowerCase();
    if (!/^\S+@\S+\.\S+$/.test(email)) {
      return problem(422, 'Validation failed', { email: ['Adresse e-mail invalide.'] });
    }
    const exists =
      state.users.some((u) => u.email === email) ||
      state.invitations.some((i) => i.email === email && i.status === 'Pending');
    if (exists) return problem(409, 'Conflict');
    const invitation: Invitation = {
      id: `20000000-0000-4000-8000-${String(state.nextId++).padStart(12, '0')}`,
      email,
      role: req.role,
      expiresAt: new Date(Date.now() + 7 * 86_400_000).toISOString(),
      acceptedAt: null,
      status: 'Pending',
    };
    state.invitations.unshift(invitation);
    return ok(invitation, 201);
  }
  if (method === 'DELETE' && path.startsWith('/invitations/')) {
    const index = state.invitations.findIndex(
      (i) => i.id === path.split('/')[2] && i.status === 'Pending',
    );
    if (index < 0) return problem(404, 'Not found');
    state.invitations.splice(index, 1);
    return ok(null, 204);
  }

  if (method === 'GET' && path === '/tenants') return ok(paged(state.tenants, query));
  if (method === 'POST' && path === '/tenants') {
    const req = body as CreateTenantRequest;
    if (!/^[a-z0-9]+(-[a-z0-9]+)*$/.test(req.slug ?? '')) {
      return problem(422, 'Validation failed', { slug: ['Slug invalide.'] });
    }
    if (state.tenants.some((t) => t.slug === req.slug)) return problem(409, 'Conflict');
    const tenant: Tenant = {
      id: `00000000-0000-4000-8000-${String(state.nextId++).padStart(12, '0')}`,
      name: req.name,
      slug: req.slug,
      isActive: true,
      createdAt: new Date().toISOString(),
    };
    state.tenants.unshift(tenant);
    return ok(tenant, 201);
  }
  if (method === 'PATCH' && path.startsWith('/tenants/')) {
    const tenant = state.tenants.find((t) => t.id === path.split('/')[2]);
    if (!tenant) return problem(404, 'Not found');
    const patch = body as UpdateTenantRequest;
    if (patch.name !== undefined) tenant.name = patch.name;
    if (patch.isActive !== undefined) tenant.isActive = patch.isActive;
    return ok({ ...tenant });
  }

  return problem(404, 'Not found');
}

export const mockBackendInterceptor: HttpInterceptorFn = (req) => {
  if (!req.url.startsWith(API_BASE)) return of(new HttpResponse({ status: 404 }));
  const url = new URL(req.urlWithParams, 'http://mock.local');
  const path = url.pathname.slice(API_BASE.length);
  return handle(req.method, path, url.searchParams, req.body).pipe(delay(latencyMs));
};
