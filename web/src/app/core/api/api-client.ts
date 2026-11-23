import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import {
  AccessToken,
  CreateInvitationRequest,
  CreateTenantRequest,
  DemoRole,
  Invitation,
  Me,
  Page,
  Provider,
  Tenant,
  UpdateTenantRequest,
  UpdateUserRequest,
  User,
} from './models';

export const API_BASE = '/api/v1';

/** Typed client for the phase 1 contract (docs/api/openapi.json). */
@Injectable({ providedIn: 'root' })
export class ApiClient {
  private readonly http = inject(HttpClient);

  /** Full-page navigation target that starts the OAuth flow (a 302 to the provider). */
  externalLoginUrl(provider: Provider, invitation?: string | null): string {
    if (environment.mockExternalLoginUrl) return environment.mockExternalLoginUrl(invitation);
    const query = invitation ? `?invitation=${encodeURIComponent(invitation)}` : '';
    return `${API_BASE}/auth/external/${provider}${query}`;
  }

  refresh(): Observable<AccessToken> {
    return this.http.post<AccessToken>(`${API_BASE}/auth/refresh`, null);
  }

  demoLogin(role: DemoRole): Observable<AccessToken> {
    return this.http.post<AccessToken>(`${API_BASE}/auth/demo`, { role });
  }

  logout(): Observable<void> {
    return this.http.post<void>(`${API_BASE}/auth/logout`, null);
  }

  getMe(): Observable<Me> {
    return this.http.get<Me>(`${API_BASE}/me`);
  }

  listTenants(page = 1, pageSize = 20): Observable<Page<Tenant>> {
    return this.http.get<Page<Tenant>>(`${API_BASE}/tenants`, {
      params: this.paging(page, pageSize),
    });
  }

  createTenant(body: CreateTenantRequest): Observable<Tenant> {
    return this.http.post<Tenant>(`${API_BASE}/tenants`, body);
  }

  updateTenant(id: string, body: UpdateTenantRequest): Observable<Tenant> {
    return this.http.patch<Tenant>(`${API_BASE}/tenants/${id}`, body);
  }

  listUsers(page = 1, pageSize = 20): Observable<Page<User>> {
    return this.http.get<Page<User>>(`${API_BASE}/users`, { params: this.paging(page, pageSize) });
  }

  updateUser(id: string, body: UpdateUserRequest): Observable<User> {
    return this.http.patch<User>(`${API_BASE}/users/${id}`, body);
  }

  listInvitations(page = 1, pageSize = 20): Observable<Page<Invitation>> {
    return this.http.get<Page<Invitation>>(`${API_BASE}/invitations`, {
      params: this.paging(page, pageSize),
    });
  }

  createInvitation(body: CreateInvitationRequest): Observable<Invitation> {
    return this.http.post<Invitation>(`${API_BASE}/invitations`, body);
  }

  revokeInvitation(id: string): Observable<void> {
    return this.http.delete<void>(`${API_BASE}/invitations/${id}`);
  }

  private paging(page: number, pageSize: number): HttpParams {
    return new HttpParams().set('page', page).set('pageSize', pageSize);
  }
}
