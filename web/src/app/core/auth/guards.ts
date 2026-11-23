import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Role } from '../api/models';
import { AuthService, homeFor } from './auth.service';

/**
 * Guards are an ergonomic layer only: the API enforces every permission.
 * Requires a signed-in user with one of the given roles.
 */
export function roleGuard(roles: readonly Role[]): CanActivateFn {
  return async () => {
    const auth = inject(AuthService);
    const router = inject(Router);
    if (!(await auth.restoreSession())) {
      return router.createUrlTree(['/login']);
    }
    const role = auth.role();
    return role !== null && roles.includes(role) ? true : router.createUrlTree(['/forbidden']);
  };
}

/** Keeps signed-in users away from /login. */
export const guestGuard: CanActivateFn = async () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  if (!(await auth.restoreSession())) return true;
  const role = auth.role();
  return role ? router.createUrlTree([homeFor(role)]) : true;
};
