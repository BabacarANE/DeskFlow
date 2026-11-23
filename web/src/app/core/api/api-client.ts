import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import {
  AccessToken,
  Category,
  Comment,
  CreateCommentRequest,
  CreateInvitationRequest,
  CreateTenantRequest,
  CreateTicketRequest,
  DemoRole,
  Invitation,
  Me,
  Page,
  Provider,
  Tenant,
  TicketDetail,
  TicketEvent,
  TicketQuery,
  TicketSummary,
  UpdateTicketRequest,
  UpdateTenantRequest,
  UpdateUserRequest,
  User,
  UserRef,
} from './models';

export const API_BASE = '/api/v1';

/** Typed client for the phase 1 and 2 contract (docs/api/openapi.json). */
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

  listCategories(): Observable<Category[]> {
    return this.http.get<Category[]>(`${API_BASE}/categories`);
  }

  listAssignees(): Observable<UserRef[]> {
    return this.http.get<UserRef[]>(`${API_BASE}/assignees`);
  }

  listTickets(query: TicketQuery = {}): Observable<Page<TicketSummary>> {
    let params = new HttpParams();
    for (const [key, value] of Object.entries(query)) {
      if (value !== undefined && value !== null && value !== '') params = params.set(key, value);
    }
    return this.http.get<Page<TicketSummary>>(`${API_BASE}/tickets`, { params });
  }

  createTicket(body: CreateTicketRequest): Observable<TicketDetail> {
    return this.http.post<TicketDetail>(`${API_BASE}/tickets`, body);
  }

  getTicket(id: string): Observable<TicketDetail> {
    return this.http.get<TicketDetail>(`${API_BASE}/tickets/${id}`);
  }

  updateTicket(id: string, body: UpdateTicketRequest): Observable<TicketDetail> {
    return this.http.patch<TicketDetail>(`${API_BASE}/tickets/${id}`, body);
  }

  addComment(id: string, body: CreateCommentRequest): Observable<Comment> {
    return this.http.post<Comment>(`${API_BASE}/tickets/${id}/comments`, body);
  }

  listTicketEvents(id: string): Observable<TicketEvent[]> {
    return this.http.get<TicketEvent[]>(`${API_BASE}/tickets/${id}/events`);
  }

  private paging(page: number, pageSize: number): HttpParams {
    return new HttpParams().set('page', page).set('pageSize', pageSize);
  }
}
