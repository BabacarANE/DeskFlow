// Types written by hand from docs/api/openapi.json (frozen phase 1 contract).

export type Role = 'SuperAdmin' | 'TenantAdmin' | 'Agent' | 'Requester';
export type AssignableRole = Exclude<Role, 'SuperAdmin'>;
export type DemoRole = 'admin' | 'agent' | 'demandeur';
export type Provider = 'google' | 'github';

export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  /** Present for 422: field name to messages. */
  errors?: Record<string, string[]>;
}

export interface AccessToken {
  accessToken: string;
  /** Seconds. */
  expiresIn: number;
}

export interface Theme {
  colorPrimary: string;
  colorSurface: string;
  colorAccent: string;
}

export interface MeTenant {
  id: string;
  name: string;
  slug: string;
  theme: Theme;
}

export interface Me {
  id: string;
  email: string;
  displayName: string;
  avatarUrl?: string | null;
  role: Role;
  /** Null for the SuperAdmin. */
  tenant?: MeTenant | null;
  isDemo: boolean;
}

export interface Tenant {
  id: string;
  name: string;
  slug: string;
  isActive: boolean;
  createdAt: string;
}

export interface CreateTenantRequest {
  name: string;
  slug: string;
  adminEmail: string;
}

export interface UpdateTenantRequest {
  name?: string;
  isActive?: boolean;
}

export interface User {
  id: string;
  email: string;
  displayName: string;
  avatarUrl?: string | null;
  role: Role;
  isActive: boolean;
  createdAt: string;
}

export interface UpdateUserRequest {
  role?: AssignableRole;
  isActive?: boolean;
}

export type InvitationStatus = 'Pending' | 'Accepted' | 'Expired';

export interface Invitation {
  id: string;
  email: string;
  role: Role;
  expiresAt: string;
  acceptedAt?: string | null;
  status: InvitationStatus;
}

export interface CreateInvitationRequest {
  email: string;
  role: AssignableRole;
}

export interface Page<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

// Phase 2 contract: tickets, comments, categories, assignees, events.

export type TicketStatus = 'New' | 'Open' | 'Pending' | 'Resolved' | 'Closed';
export type TicketPriority = 'Low' | 'Normal' | 'High' | 'Urgent';
export type TicketSort = 'createdAt' | 'updatedAt' | 'priority' | 'number';
export type SortDirection = 'asc' | 'desc';

export interface UserRef {
  id: string;
  displayName: string;
}

export interface Category {
  id: string;
  name: string;
}

export interface TicketSummary {
  id: string;
  /** Sequential per tenant. */
  number: number;
  title: string;
  status: TicketStatus;
  priority: TicketPriority;
  category?: Category | null;
  requester: UserRef;
  assignee?: UserRef | null;
  createdAt: string;
  updatedAt: string;
  responseDueAt?: string | null;
  resolutionDueAt?: string | null;
  escalatedAt?: string | null;
}

export interface Comment {
  id: string;
  author: UserRef;
  body: string;
  isInternal: boolean;
  createdAt: string;
}

export interface TicketDetail extends TicketSummary {
  description: string;
  /** Opaque optimistic concurrency token, sent back as is in PATCH. */
  rowVersion: string;
  firstResponseAt?: string | null;
  resolvedAt?: string | null;
  /** Oldest first. */
  comments: Comment[];
}

export interface CreateTicketRequest {
  title: string;
  description: string;
  categoryId?: string | null;
  /** Normal when omitted. */
  priority?: TicketPriority;
}

export interface UpdateTicketRequest {
  rowVersion: string;
  status?: TicketStatus;
  priority?: TicketPriority;
  /** Null removes the assignee. */
  assigneeId?: string | null;
  /** Null removes the category. */
  categoryId?: string | null;
}

export interface CreateCommentRequest {
  body: string;
  isInternal?: boolean;
}

export type TicketEventType =
  | 'Created'
  | 'StatusChanged'
  | 'PriorityChanged'
  | 'AssigneeChanged'
  | 'CategoryChanged';

export interface TicketEvent {
  id: string;
  type: TicketEventType;
  /** Null for a system event. */
  actor?: UserRef | null;
  oldValue?: string | null;
  /** Status and priority as strings; assignee and category as the displayed name. */
  newValue?: string | null;
  at: string;
}

/** Query of GET /tickets. Absent keys are not sent. */
export interface TicketQuery {
  status?: TicketStatus;
  priority?: TicketPriority;
  assigneeId?: string;
  unassigned?: boolean;
  categoryId?: string;
  q?: string;
  sort?: TicketSort;
  direction?: SortDirection;
  page?: number;
  pageSize?: number;
}

/** Suffixes of the `type` URI of the 409 ProblemDetails (contract of PATCH /tickets/{id}). */
export const PROBLEM_INVALID_TRANSITION = 'invalid-transition';
export const PROBLEM_CONCURRENCY_CONFLICT = 'concurrency-conflict';

/** Codes of /auth/callback?error=<code> listed in the contract. */
export const AUTH_ERROR_CODES = [
  'invitation_required',
  'invitation_invalid',
  'email_mismatch',
  'email_not_verified',
  'account_disabled',
  'tenant_suspended',
] as const;
export type AuthErrorCode = (typeof AUTH_ERROR_CODES)[number];
