import {
  HttpClient,
  HttpErrorResponse,
  provideHttpClient,
  withInterceptors,
} from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { authInterceptor } from './auth.interceptor';
import { AuthService } from './auth.service';

describe('authInterceptor', () => {
  let http: HttpClient;
  let controller: HttpTestingController;
  let navigate: ReturnType<typeof vi.fn>;
  const auth = {
    accessToken: 'old' as string | null,
    refreshToken: vi.fn<() => Observable<string>>(),
    clear: vi.fn(),
  };

  beforeEach(() => {
    auth.accessToken = 'old';
    auth.refreshToken.mockReset().mockReturnValue(of('new'));
    auth.clear.mockReset();
    navigate = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        { provide: AuthService, useValue: auth },
        { provide: Router, useValue: { navigate } },
      ],
    });
    http = TestBed.inject(HttpClient);
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  it('adds the Bearer token to API calls', () => {
    http.get('/api/v1/me').subscribe();
    const req = controller.expectOne('/api/v1/me');
    expect(req.request.headers.get('Authorization')).toBe('Bearer old');
    req.flush({});
  });

  it('sends no Authorization header without a token', () => {
    auth.accessToken = null;
    http.get('/api/v1/me').subscribe();
    const req = controller.expectOne('/api/v1/me');
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush({});
  });

  it('leaves requests outside the API untouched', () => {
    http.get('/assets/x.json').subscribe();
    const req = controller.expectOne('/assets/x.json');
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush({});
  });

  it('does not send the token to anonymous auth endpoints, nor refresh on their 401', () => {
    let status = 0;
    http
      .post('/api/v1/auth/refresh', null)
      .subscribe({ error: (e: HttpErrorResponse) => (status = e.status) });
    const req = controller.expectOne('/api/v1/auth/refresh');
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush({}, { status: 401, statusText: 'Unauthorized' });

    expect(status).toBe(401);
    expect(auth.refreshToken).not.toHaveBeenCalled();
  });

  it('renews the token once on a 401, then replays the request with the new token', () => {
    let result: unknown;
    http.get('/api/v1/users').subscribe((r) => (result = r));
    controller.expectOne('/api/v1/users').flush({}, { status: 401, statusText: 'Unauthorized' });

    const replay = controller.expectOne('/api/v1/users');
    expect(replay.request.headers.get('Authorization')).toBe('Bearer new');
    replay.flush({ ok: true });

    expect(result).toEqual({ ok: true });
    expect(auth.refreshToken).toHaveBeenCalledTimes(1);
  });

  it('does not loop when the replayed request is still rejected', () => {
    let status = 0;
    http.get('/api/v1/users').subscribe({ error: (e: HttpErrorResponse) => (status = e.status) });
    controller.expectOne('/api/v1/users').flush({}, { status: 401, statusText: 'Unauthorized' });
    controller.expectOne('/api/v1/users').flush({}, { status: 401, statusText: 'Unauthorized' });

    expect(status).toBe(401);
    expect(auth.refreshToken).toHaveBeenCalledTimes(1);
    controller.expectNone('/api/v1/users');
  });

  it('drops the session and goes to /login when the renewal itself is refused', () => {
    auth.refreshToken.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 401, statusText: 'Unauthorized' })),
    );
    let failed = false;
    http.get('/api/v1/users').subscribe({ error: () => (failed = true) });
    controller.expectOne('/api/v1/users').flush({}, { status: 401, statusText: 'Unauthorized' });

    expect(failed).toBe(true);
    expect(auth.clear).toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith(['/login']);
  });

  it('does not try to refresh on other errors', () => {
    let status = 0;
    http.get('/api/v1/users').subscribe({ error: (e: HttpErrorResponse) => (status = e.status) });
    controller.expectOne('/api/v1/users').flush({}, { status: 403, statusText: 'Forbidden' });

    expect(status).toBe(403);
    expect(auth.refreshToken).not.toHaveBeenCalled();
  });
});
