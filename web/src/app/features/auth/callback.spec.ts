import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { AUTH_ERROR_CODES, Role } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { AUTH_ERROR_MESSAGES, UNKNOWN_AUTH_ERROR_MESSAGE, authErrorMessage } from './auth-errors';
import { Callback } from './callback';

describe('Callback', () => {
  const role = signal<Role | null>(null);
  const auth = { restoreSession: vi.fn(), role };

  let query = '';

  beforeEach(() => {
    auth.restoreSession.mockReset();
    role.set(null);
    query = '';
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: auth },
        {
          provide: ActivatedRoute,
          useValue: {
            get snapshot() {
              return {
                queryParamMap: convertToParamMap(Object.fromEntries(new URLSearchParams(query))),
              };
            },
          },
        },
      ],
    });
  });

  async function open(search: string) {
    query = search;
    const fixture = TestBed.createComponent(Callback);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('has a French message for each error code of the contract', () => {
    expect(AUTH_ERROR_CODES).toHaveLength(6);
    for (const code of AUTH_ERROR_CODES) {
      expect(AUTH_ERROR_MESSAGES[code].length).toBeGreaterThan(20);
      expect(authErrorMessage(code)).toBe(AUTH_ERROR_MESSAGES[code]);
    }
    expect(new Set(AUTH_ERROR_CODES.map((c) => AUTH_ERROR_MESSAGES[c])).size).toBe(6);
  });

  it.each(AUTH_ERROR_CODES)('shows the message of error=%s and a way back', async (code) => {
    const el = await open(`error=${code}`);
    await vi.waitFor(() =>
      expect(el.querySelector('[role="alert"]')?.textContent).toContain(AUTH_ERROR_MESSAGES[code]),
    );
    expect(el.querySelector('a')?.getAttribute('href')).toBe('/login');
    expect(auth.restoreSession).not.toHaveBeenCalled();
  });

  it('shows a generic message for an unknown error code', async () => {
    const el = await open('error=whatever');
    await vi.waitFor(() =>
      expect(el.querySelector('[role="alert"]')?.textContent).toContain(UNKNOWN_AUTH_ERROR_MESSAGE),
    );
  });

  it.each([
    ['SuperAdmin', '/platform/tenants'],
    ['TenantAdmin', '/admin'],
    ['Agent', '/tickets'],
    ['Requester', '/tickets'],
  ] as const)('restores the session and sends a %s to %s', async (userRole, target) => {
    auth.restoreSession.mockResolvedValue(true);
    role.set(userRole);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    await open('');
    await vi.waitFor(() => expect(navigate).toHaveBeenCalledWith(target, { replaceUrl: true }));
  });

  it('shows an error when no session can be restored', async () => {
    auth.restoreSession.mockResolvedValue(false);
    const el = await open('');
    await vi.waitFor(() =>
      expect(el.querySelector('[role="alert"]')?.textContent).toContain('session'),
    );
  });
});
