import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../api/api-client';
import { ApiError } from '../api/api-error';
import { errorInterceptor } from '../api/error.interceptor';
import { mockBackendInterceptor, resetMockBackend } from './mock-backend.interceptor';

/** The simulated backend must honour the contract rules the screens rely on. */
describe('mock backend', () => {
  let api: ApiClient;

  beforeEach(() => {
    sessionStorage.clear();
    resetMockBackend();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([errorInterceptor, mockBackendInterceptor]))],
    });
    api = TestBed.inject(ApiClient);
  });

  afterEach(() => sessionStorage.clear());

  const status = async (promise: Promise<unknown>) => {
    try {
      await promise;
      return 200;
    } catch (e) {
      return (e as ApiError).status;
    }
  };

  it('refuses the session endpoints before sign-in', async () => {
    expect(await status(firstValueFrom(api.refresh()))).toBe(401);
    expect(await status(firstValueFrom(api.getMe()))).toBe(401);
  });

  it('signs in with a demo role and reports isDemo', async () => {
    await firstValueFrom(api.demoLogin('admin'));
    const me = await firstValueFrom(api.getMe());
    expect(me.role).toBe('TenantAdmin');
    expect(me.isDemo).toBe(true);
    expect((await firstValueFrom(api.refresh())).accessToken).toBeTruthy();
  });

  it('enforces roles: a requester cannot list users', async () => {
    await firstValueFrom(api.demoLogin('demandeur'));
    expect(await status(firstValueFrom(api.listUsers()))).toBe(403);
  });

  it('invites a user, rejects duplicates with 409 and bad e-mails with 422', async () => {
    await firstValueFrom(api.demoLogin('admin'));
    const created = await firstValueFrom(
      api.createInvitation({ email: 'new@acme.test', role: 'Agent' }),
    );
    expect(created.status).toBe('Pending');
    expect(
      await status(firstValueFrom(api.createInvitation({ email: 'new@acme.test', role: 'Agent' }))),
    ).toBe(409);
    expect(
      await status(firstValueFrom(api.createInvitation({ email: 'nope', role: 'Agent' }))),
    ).toBe(422);
    await firstValueFrom(api.revokeInvitation(created.id));
    const list = await firstValueFrom(api.listInvitations());
    expect(list.items.some((i) => i.id === created.id)).toBe(false);
  });

  it('does not let an admin modify their own account', async () => {
    await firstValueFrom(api.demoLogin('admin'));
    const me = await firstValueFrom(api.getMe());
    expect(await status(firstValueFrom(api.updateUser(me.id, { isActive: false })))).toBe(409);
  });

  it('serves the SuperAdmin tenant screens only to the SuperAdmin', async () => {
    await firstValueFrom(api.demoLogin('admin'));
    expect(await status(firstValueFrom(api.listTenants()))).toBe(403);

    sessionStorage.setItem('deskflow.mock.role', 'superadmin');
    const created = await firstValueFrom(
      api.createTenant({ name: 'Globex', slug: 'globex', adminEmail: 'boss@globex.test' }),
    );
    expect(created.isActive).toBe(true);
    const suspended = await firstValueFrom(api.updateTenant(created.id, { isActive: false }));
    expect(suspended.isActive).toBe(false);
    expect(
      await status(
        firstValueFrom(api.createTenant({ name: 'G2', slug: 'globex', adminEmail: 'a@b.test' })),
      ),
    ).toBe(409);
  });
});
