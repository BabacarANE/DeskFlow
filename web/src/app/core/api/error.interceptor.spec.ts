import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ApiError } from './api-error';
import { errorInterceptor } from './error.interceptor';

describe('errorInterceptor', () => {
  let http: HttpClient;
  let controller: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([errorInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  function failWith(body: object | string, status: number): ApiError {
    let caught: unknown;
    http.get('/api/v1/x').subscribe({ error: (e: unknown) => (caught = e) });
    controller.expectOne('/api/v1/x').flush(body, { status, statusText: 'err' });
    expect(caught).toBeInstanceOf(ApiError);
    return caught as ApiError;
  }

  it('parses a ProblemDetails 422 with its field errors', () => {
    const error = failWith(
      { title: 'Validation', status: 422, errors: { Email: ['invalid'] } },
      422,
    );
    expect(error.status).toBe(422);
    expect(error.message).toBe('Certaines informations sont invalides.');
    expect(error.fieldErrors).toEqual({ Email: ['invalid'] });
    expect(error.problem?.title).toBe('Validation');
  });

  it.each([
    [401, 'Votre session a expiré. Reconnectez-vous.'],
    [403, 'Vous n’avez pas le droit d’effectuer cette action.'],
    [404, 'Élément introuvable.'],
    [409, 'Cette opération entre en conflit avec l’état actuel des données.'],
    [429, 'Trop de tentatives. Réessayez dans un instant.'],
    [500, 'Le serveur a rencontré une erreur. Réessayez plus tard.'],
  ])('maps status %i to a French message', (status, message) => {
    expect(failWith({ title: 't' }, status).message).toBe(message);
  });

  it('copes with a non-JSON error body', () => {
    const error = failWith('<html>Bad gateway</html>', 502);
    expect(error.problem).toBeNull();
    expect(error.fieldErrors).toEqual({});
  });

  it('reports an unreachable server', () => {
    let caught: unknown;
    http.get('/api/v1/x').subscribe({ error: (e: unknown) => (caught = e) });
    controller.expectOne('/api/v1/x').error(new ProgressEvent('error'));
    expect((caught as ApiError).status).toBe(0);
    expect((caught as ApiError).message).toContain('Impossible de joindre le serveur');
  });

  it('lets successful responses through', () => {
    let result: unknown;
    http.get('/api/v1/x').subscribe((r) => (result = r));
    controller.expectOne('/api/v1/x').flush({ ok: 1 });
    expect(result).toEqual({ ok: 1 });
  });
});
