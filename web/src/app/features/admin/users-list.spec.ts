import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError } from '../../core/api/api-error';
import { Page, User } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { UsersList } from './users-list';

function user(id: string, overrides: Partial<User> = {}): User {
  return {
    id,
    email: `${id}@acme.test`,
    displayName: `Utilisateur ${id}`,
    role: 'Agent',
    isActive: true,
    createdAt: '2026-11-01T09:00:00Z',
    ...overrides,
  };
}

const page = (items: User[]): Page<User> => ({ items, total: items.length, page: 1, pageSize: 20 });

describe('UsersList', () => {
  const api = { listUsers: vi.fn(), updateUser: vi.fn() };
  let fixture: ComponentFixture<UsersList>;
  const el = () => fixture.nativeElement as HTMLElement;
  const button = (text: string) =>
    Array.from(el().querySelectorAll('button')).find((b) => b.textContent?.includes(text));

  async function render() {
    fixture = TestBed.createComponent(UsersList);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  beforeEach(() => {
    api.listUsers.mockReset();
    api.updateUser.mockReset();
    TestBed.configureTestingModule({
      providers: [
        { provide: ApiClient, useValue: api },
        { provide: AuthService, useValue: { user: () => ({ id: 'me' }) } },
      ],
    });
  });

  it('shows a loading state while the request is pending', async () => {
    api.listUsers.mockReturnValue(new Subject<Page<User>>());
    await render();
    expect(el().querySelector('[role="status"]')?.textContent).toContain('Chargement');
    expect(el().querySelector('table')).toBeNull();
  });

  it('shows an empty state', async () => {
    api.listUsers.mockReturnValue(of(page([])));
    await render();
    expect(el().textContent).toContain('Aucun utilisateur pour le moment.');
  });

  it('shows an error state and retries', async () => {
    api.listUsers.mockReturnValueOnce(
      throwError(() => new ApiError(500, 'Le serveur a rencontré une erreur.', null)),
    );
    await render();
    expect(el().querySelector('[role="alert"]')?.textContent).toContain(
      'Le serveur a rencontré une erreur.',
    );

    api.listUsers.mockReturnValue(of(page([user('u1')])));
    button('Réessayer')?.click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(el().querySelectorAll('tbody tr')).toHaveLength(1);
  });

  it('lists users and prevents acting on yourself', async () => {
    api.listUsers.mockReturnValue(of(page([user('me', { role: 'TenantAdmin' }), user('u2')])));
    await render();

    expect(el().querySelectorAll('tbody tr')).toHaveLength(2);
    expect(el().textContent).toContain('(vous)');
    // Only the other user gets a role selector and a deactivate button.
    expect(el().querySelectorAll('select')).toHaveLength(1);
    expect(el().querySelectorAll('button')).toHaveLength(1);
  });

  it('labels the role selector for assistive technologies', async () => {
    api.listUsers.mockReturnValue(of(page([user('u2')])));
    await render();
    const select = el().querySelector('select') as HTMLSelectElement;
    expect(el().querySelector(`label[for="${select.id}"]`)?.textContent).toContain(
      'Utilisateur u2',
    );
  });

  it('changes a role through PATCH /users/{id}', async () => {
    api.listUsers.mockReturnValue(of(page([user('u2')])));
    api.updateUser.mockReturnValue(of(user('u2', { role: 'Requester' })));
    await render();

    const select = el().querySelector('select') as HTMLSelectElement;
    select.value = 'Requester';
    select.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(api.updateUser).toHaveBeenCalledWith('u2', { role: 'Requester' });
    expect(el().querySelector('tbody td:nth-child(2)')?.textContent).toBeDefined();
  });

  it('reverts the selector and shows the error when the change is refused', async () => {
    api.listUsers.mockReturnValue(of(page([user('u2')])));
    api.updateUser.mockReturnValue(
      throwError(
        () => new ApiError(403, 'Vous n’avez pas le droit d’effectuer cette action.', null),
      ),
    );
    await render();

    const select = el().querySelector('select') as HTMLSelectElement;
    select.value = 'Requester';
    select.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(select.value).toBe('Agent');
    expect(el().querySelector('[role="alert"]')?.textContent).toContain('pas le droit');
  });

  it('deactivates a user only after a second confirming click', async () => {
    api.listUsers.mockReturnValue(of(page([user('u2')])));
    api.updateUser.mockReturnValue(of(user('u2', { isActive: false })));
    await render();

    button('Désactiver')?.click();
    fixture.detectChanges();
    expect(api.updateUser).not.toHaveBeenCalled();

    button('Confirmer')?.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(api.updateUser).toHaveBeenCalledWith('u2', { isActive: false });
    expect(el().textContent).toContain('Désactivé');
    expect(button('Réactiver')).toBeDefined();
  });

  it('can cancel a deactivation', async () => {
    api.listUsers.mockReturnValue(of(page([user('u2')])));
    await render();
    button('Désactiver')?.click();
    fixture.detectChanges();
    button('Annuler')?.click();
    fixture.detectChanges();
    expect(button('Désactiver')).toBeDefined();
    expect(api.updateUser).not.toHaveBeenCalled();
  });
});
