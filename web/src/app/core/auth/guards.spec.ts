import { TestBed } from '@angular/core/testing';
import {
  ActivatedRouteSnapshot,
  RouterStateSnapshot,
  UrlTree,
  provideRouter,
} from '@angular/router';
import { Role } from '../api/models';
import { AuthService } from './auth.service';
import { guestGuard, roleGuard } from './guards';

describe('guards', () => {
  let session: boolean;
  let role: Role | null;

  beforeEach(() => {
    session = true;
    role = 'Agent';
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: AuthService,
          useValue: { restoreSession: async () => session, role: () => role },
        },
      ],
    });
  });

  async function run(guard: ReturnType<typeof roleGuard>): Promise<boolean | string> {
    const result = await TestBed.runInInjectionContext(() =>
      guard({} as ActivatedRouteSnapshot, { url: '/x' } as RouterStateSnapshot),
    );
    return result instanceof UrlTree ? result.toString() : (result as boolean);
  }

  it('lets a user with an allowed role through', async () => {
    expect(await run(roleGuard(['Agent', 'TenantAdmin']))).toBe(true);
  });

  it('redirects an anonymous visitor to /login', async () => {
    session = false;
    role = null;
    expect(await run(roleGuard(['Agent']))).toBe('/login');
  });

  it('redirects a user with the wrong role to /forbidden', async () => {
    role = 'Requester';
    expect(await run(roleGuard(['TenantAdmin']))).toBe('/forbidden');
  });

  it('lets anonymous visitors see /login', async () => {
    session = false;
    role = null;
    expect(await run(guestGuard)).toBe(true);
  });

  it('sends a signed-in user from /login to their landing page', async () => {
    role = 'TenantAdmin';
    expect(await run(guestGuard)).toBe('/admin');
    role = 'SuperAdmin';
    expect(await run(guestGuard)).toBe('/platform/tenants');
    role = 'Requester';
    expect(await run(guestGuard)).toBe('/tickets');
  });
});
