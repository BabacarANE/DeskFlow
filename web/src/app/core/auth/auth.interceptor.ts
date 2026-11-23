import {
  HttpErrorResponse,
  HttpHandlerFn,
  HttpInterceptorFn,
  HttpRequest,
} from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, switchMap, throwError } from 'rxjs';
import { API_BASE } from '../api/api-client';
import { AuthService } from './auth.service';

/** Endpoints that never carry a Bearer token and never trigger a refresh. */
function isAnonymousEndpoint(url: string): boolean {
  return (
    url.startsWith(`${API_BASE}/auth/refresh`) ||
    url.startsWith(`${API_BASE}/auth/demo`) ||
    url.startsWith(`${API_BASE}/auth/external`)
  );
}

function withBearer(req: HttpRequest<unknown>, token: string | null): HttpRequest<unknown> {
  return token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req;
}

/** Adds the Bearer token; on a 401 renews it once (single-flight) and replays the request. */
export const authInterceptor: HttpInterceptorFn = (req, next: HttpHandlerFn) => {
  if (!req.url.startsWith(API_BASE) || isAnonymousEndpoint(req.url)) return next(req);

  const auth = inject(AuthService);
  const router = inject(Router);

  return next(withBearer(req, auth.accessToken)).pipe(
    catchError((error: unknown) => {
      if (!(error instanceof HttpErrorResponse) || error.status !== 401) {
        return throwError(() => error);
      }
      return auth.refreshToken().pipe(
        switchMap((token) => next(withBearer(req, token))),
        catchError((refreshError: unknown) => {
          const stillUnauthorized =
            refreshError instanceof HttpErrorResponse && refreshError.status === 401;
          if (stillUnauthorized) {
            auth.clear();
            void router.navigate(['/login']);
          }
          return throwError(() => refreshError);
        }),
      );
    }),
  );
};
