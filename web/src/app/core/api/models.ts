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
