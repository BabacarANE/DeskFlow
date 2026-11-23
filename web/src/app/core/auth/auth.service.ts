import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, catchError, finalize, firstValueFrom, map, of, shareReplay, tap } from 'rxjs';
import { ApiClient } from '../api/api-client';
import { DemoRole, Me, Role } from '../api/models';
import { ThemeService } from '../theme/theme.service';

/** Where each role lands after signing in. */
export function homeFor(role: Role): string {
  switch (role) {
    case 'SuperAdmin':
      return '/platform/tenants';
    case 'TenantAdmin':
      return '/admin';
    default:
      return '/tickets';
  }
}

/**
 * Session state. The access token lives in memory only (never in storage); the refresh token
 * is an HttpOnly cookie managed by the API.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly api = inject(ApiClient);
  private readonly theme = inject(ThemeService);
  private readonly router = inject(Router);

  private token: string | null = null;
  private readonly currentUser = signal<Me | null>(null);
  private session?: Promise<boolean>;
  private refreshing?: Observable<string>;
  /** Bumped by clear(): a renewal started before it must not bring the token back. */
  private sessionGeneration = 0;

  readonly user = this.currentUser.asReadonly();
  readonly isAuthenticated = computed(() => this.currentUser() !== null);
  readonly role = computed(() => this.currentUser()?.role ?? null);
  readonly isDemo = computed(() => this.currentUser()?.isDemo ?? false);

  get accessToken(): string | null {
    return this.token;
  }

  /**
   * Resolves once the session is known (true when signed in). The refresh call runs only once;
   * later callers share its result, so rotation never sees a reused cookie.
   */
  restoreSession(): Promise<boolean> {
    this.session ??= firstValueFrom(
      this.api.refresh().pipe(
        tap((r) => (this.token = r.accessToken)),
        map(() => true),
        catchError(() => of(false)),
      ),
    ).then((ok) => (ok ? this.loadProfile() : false));
    return this.session;
  }

  async loginDemo(role: DemoRole): Promise<Me> {
    const result = await firstValueFrom(this.api.demoLogin(role));
    this.token = result.accessToken;
    this.session = Promise.resolve(true);
    const me = await firstValueFrom(this.api.getMe());
    this.setUser(me);
    return me;
  }

  /** Single-flight token renewal, used by the interceptor after a 401. */
  refreshToken(): Observable<string> {
    const generation = this.sessionGeneration;
    this.refreshing ??= this.api.refresh().pipe(
      map((r) => r.accessToken),
      tap((token) => {
        if (generation === this.sessionGeneration) this.token = token;
      }),
      finalize(() => (this.refreshing = undefined)),
      shareReplay({ bufferSize: 1, refCount: false }),
    );
    return this.refreshing;
  }

  async logout(): Promise<void> {
    try {
      await firstValueFrom(this.api.logout());
    } catch {
      // The session is dropped locally even if the server cannot be reached.
    }
    this.clear();
    await this.router.navigate(['/login']);
  }

  /** Forgets the session locally (also used when a refresh fails). */
  clear(): void {
    this.sessionGeneration++;
    this.refreshing = undefined;
    this.token = null;
    this.currentUser.set(null);
    this.session = Promise.resolve(false);
    this.theme.reset();
  }

  private async loadProfile(): Promise<boolean> {
    try {
      this.setUser(await firstValueFrom(this.api.getMe()));
      return true;
    } catch {
      this.clear();
      return false;
    }
  }

  private setUser(me: Me): void {
    this.currentUser.set(me);
    this.theme.apply(me.tenant?.theme);
  }
}
