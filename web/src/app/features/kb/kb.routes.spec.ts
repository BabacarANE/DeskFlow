import { TestBed } from '@angular/core/testing';
import {
  ActivatedRouteSnapshot,
  CanActivateFn,
  RouterStateSnapshot,
  UrlTree,
  provideRouter,
} from '@angular/router';
import { routes } from '../../app.routes';
import { Role } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { KB_ROUTES } from './kb.routes';

/** Role guards of the knowledge base: ergonomic only, the API enforces the same rules. */
describe('knowledge base routes', () => {
  let role: Role | null;

  beforeEach(() => {
    role = 'Requester';
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: AuthService,
          useValue: { restoreSession: async () => role !== null, role: () => role },
        },
      ],
    });
  });

  async function allowed(guards: CanActivateFn[] | undefined): Promise<boolean | string> {
    for (const guard of guards ?? []) {
      const result = await TestBed.runInInjectionContext(() =>
        guard({} as ActivatedRouteSnapshot, { url: '/kb' } as RouterStateSnapshot),
      );
      if (result instanceof UrlTree) return result.toString();
      if (result !== true) return false;
    }
    return true;
  }

  const route = (path: string) => KB_ROUTES.find((r) => r.path === path);
  const kbRoot = () => routes.find((r) => r.path === 'kb');

  it('lets the three company roles read, and not the SuperAdmin', async () => {
    for (const r of ['Requester', 'Agent', 'TenantAdmin'] as const) {
      role = r;
      expect(await allowed(kbRoot()?.canActivate as CanActivateFn[]), r).toBe(true);
    }
    role = 'SuperAdmin';
    expect(await allowed(kbRoot()?.canActivate as CanActivateFn[])).toBe('/forbidden');
    role = null;
    expect(await allowed(kbRoot()?.canActivate as CanActivateFn[])).toBe('/login');
  });

  it('keeps /kb/new and /kb/:id/edit away from a requester', async () => {
    for (const path of ['new', ':id/edit']) {
      role = 'Requester';
      expect(await allowed(route(path)?.canActivate as CanActivateFn[]), path).toBe('/forbidden');
      for (const r of ['Agent', 'TenantAdmin'] as const) {
        role = r;
        expect(await allowed(route(path)?.canActivate as CanActivateFn[]), path + r).toBe(true);
      }
    }
  });

  it('declares "new" before ":id" so it is not read as an article id', () => {
    const paths = KB_ROUTES.map((r) => r.path);
    expect(paths.indexOf('new')).toBeLessThan(paths.indexOf(':id'));
    expect(paths.indexOf(':id/edit')).toBeLessThan(paths.indexOf(':id'));
  });

  it('is a lazy route of the application', () => {
    expect(kbRoot()?.loadChildren).toBeTypeOf('function');
  });
});
