import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { errorInterceptor } from '../api/error.interceptor';
import { Me } from '../api/models';
import { ThemeService } from '../theme/theme.service';
import { authInterceptor } from './auth.interceptor';
import { AuthService, homeFor } from './auth.service';

const ME: Me = {
  id: 'u1',
  email: 'admin@acme.test',
  displayName: 'Alice',
  role: 'TenantAdmin',
  isDemo: true,
  tenant: {
    id: 't1',
    name: 'Acme',
    slug: 'acme',
    theme: { colorPrimary: '#102030', colorSurface: '#FAFAFA', colorAccent: '#FFD400' },
  },
};

describe('AuthService', () => {
  let auth: AuthService;
  let http: HttpTestingController;

  /** The next request may be issued after a microtask (promise chains), so wait for it. */
  async function respond(url: string, body: object | null, status = 200) {
    const req = await vi.waitFor(() => http.expectOne(url));
    req.flush(body, { status, statusText: String(status) });
    return req.request;
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(withInterceptors([errorInterceptor, authInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    auth = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    TestBed.inject(ThemeService).reset();
    http.verify();
  });

  it('restores the session: refresh, then /me with the Bearer token, then the theme', async () => {
    const restored = auth.restoreSession();
    await respond('/api/v1/auth/refresh', { accessToken: 'tok-1', expiresIn: 900 });
    const meRequest = await respond('/api/v1/me', ME);

    expect(await restored).toBe(true);
    expect(meRequest.headers.get('Authorization')).toBe('Bearer tok-1');
    expect(auth.user()).toEqual(ME);
    expect(auth.role()).toBe('TenantAdmin');
    expect(auth.isDemo()).toBe(true);
    expect(document.documentElement.style.getPropertyValue('--color-primary')).toBe('#102030');
  });

  it('stays anonymous when the refresh cookie is missing or invalid', async () => {
    const restored = auth.restoreSession();
    await respond('/api/v1/auth/refresh', { title: 'Unauthorized' }, 401);

    expect(await restored).toBe(false);
    expect(auth.isAuthenticated()).toBe(false);
    expect(auth.accessToken).toBeNull();
  });

  it('calls refresh only once however many callers wait for the session', async () => {
    const first = auth.restoreSession();
    const second = auth.restoreSession();
    await respond('/api/v1/auth/refresh', { accessToken: 'tok-1', expiresIn: 900 });
    await respond('/api/v1/me', ME);

    expect(await first).toBe(true);
    expect(await second).toBe(true);
  });

  it('signs in with a demo role', async () => {
    const login = auth.loginDemo('admin');
    const request = await respond('/api/v1/auth/demo', { accessToken: 'tok-d', expiresIn: 900 });
    await respond('/api/v1/me', ME);

    expect(request.body).toEqual({ role: 'admin' });
    expect((await login).role).toBe('TenantAdmin');
    expect(await auth.restoreSession()).toBe(true);
  });

  it('keeps the access token in memory only', async () => {
    const restored = auth.restoreSession();
    await respond('/api/v1/auth/refresh', { accessToken: 'secret-token', expiresIn: 900 });
    await respond('/api/v1/me', ME);
    await restored;

    expect(JSON.stringify({ ...localStorage })).not.toContain('secret-token');
    expect(JSON.stringify({ ...sessionStorage })).not.toContain('secret-token');
  });

  it('shares one in-flight token renewal between concurrent callers', async () => {
    let tokens: string[] = [];
    auth.refreshToken().subscribe((t) => tokens.push(t));
    auth.refreshToken().subscribe((t) => tokens.push(t));
    const req = http.expectOne('/api/v1/auth/refresh');
    req.flush({ accessToken: 'tok-2', expiresIn: 900 });

    expect(tokens).toEqual(['tok-2', 'tok-2']);
    expect(auth.accessToken).toBe('tok-2');
    tokens = [];
    auth.refreshToken().subscribe();
    http.expectOne('/api/v1/auth/refresh').flush({ accessToken: 'tok-3', expiresIn: 900 });
    expect(auth.accessToken).toBe('tok-3');
  });

  it('logs out: calls the API, drops the session and the theme, goes to /login', async () => {
    const restored = auth.restoreSession();
    await respond('/api/v1/auth/refresh', { accessToken: 'tok-1', expiresIn: 900 });
    await respond('/api/v1/me', ME);
    await restored;
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

    const done = auth.logout();
    const request = await respond('/api/v1/auth/logout', null, 204);
    await done;

    expect(request.headers.get('Authorization')).toBe('Bearer tok-1');
    expect(auth.user()).toBeNull();
    expect(auth.accessToken).toBeNull();
    expect(document.documentElement.style.getPropertyValue('--color-primary')).toBe('');
    expect(navigate).toHaveBeenCalledWith(['/login']);
    expect(await auth.restoreSession()).toBe(false);
  });

  it('still clears the local session when the logout call fails', async () => {
    vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const login = auth.loginDemo('admin');
    await respond('/api/v1/auth/demo', { accessToken: 'tok-d', expiresIn: 900 });
    await respond('/api/v1/me', ME);
    await login;

    const done = auth.logout();
    await respond('/api/v1/auth/logout', {}, 500);
    await done;

    expect(auth.isAuthenticated()).toBe(false);
  });
});

describe('homeFor', () => {
  it('sends each role to its landing page', () => {
    expect(homeFor('SuperAdmin')).toBe('/platform/tenants');
    expect(homeFor('TenantAdmin')).toBe('/admin');
    expect(homeFor('Agent')).toBe('/tickets');
    expect(homeFor('Requester')).toBe('/tickets');
  });
});
