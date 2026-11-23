import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { ApiError } from '../../core/api/api-error';
import { AuthService } from '../../core/auth/auth.service';
import { Login } from './login';

describe('Login', () => {
  const auth = { loginDemo: vi.fn() };

  let params: Record<string, string> = {};

  beforeEach(() => {
    auth.loginDemo.mockReset();
    params = {};
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: auth },
        {
          provide: ActivatedRoute,
          useValue: {
            get queryParamMap() {
              return of(convertToParamMap(params));
            },
          },
        },
      ],
    });
  });

  async function open(invitation?: string) {
    if (invitation) params = { invitation };
    const fixture = TestBed.createComponent(Login);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const link = (el: HTMLElement, text: string) =>
    Array.from(el.querySelectorAll('a')).find((a) => a.textContent?.includes(text));

  it('links Google and GitHub to the API OAuth endpoint', async () => {
    const el = await open();
    expect(link(el, 'Google')?.getAttribute('href')).toBe('/api/v1/auth/external/google');
    expect(link(el, 'GitHub')?.getAttribute('href')).toBe('/api/v1/auth/external/github');
  });

  it('keeps the invitation token from the query string in the OAuth links', async () => {
    const el = await open('abc 123');
    expect(link(el, 'Google')?.getAttribute('href')).toBe(
      '/api/v1/auth/external/google?invitation=abc%20123',
    );
    expect(link(el, 'GitHub')?.getAttribute('href')).toContain('?invitation=abc%20123');
    expect(el.textContent).toContain('Vous avez une invitation');
  });

  it('offers the three demo profiles and starts the chosen one', async () => {
    auth.loginDemo.mockResolvedValue({ role: 'Agent' });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    const el = await open();

    const radios = el.querySelectorAll<HTMLInputElement>('input[type="radio"]');
    expect(radios).toHaveLength(3);
    const labels = Array.from(el.querySelectorAll('label')).map((l) => l.textContent ?? '');
    expect(labels[0]).toContain('Administrateur');
    expect(labels[1]).toContain('Agent');
    expect(labels[2]).toContain('Demandeur');

    radios[1].click();
    el.querySelector('form')?.dispatchEvent(new Event('submit'));
    await vi.waitFor(() => expect(auth.loginDemo).toHaveBeenCalledWith('agent'));
    await vi.waitFor(() => expect(navigate).toHaveBeenCalledWith('/tickets'));
  });

  it('shows the error when the demo is unavailable', async () => {
    auth.loginDemo.mockRejectedValue(new ApiError(404, 'Élément introuvable.', null));
    const el = await open();

    el.querySelector('form')?.dispatchEvent(new Event('submit'));
    await vi.waitFor(() =>
      expect(el.querySelector('[role="alert"]')?.textContent).toContain('Élément introuvable.'),
    );
  });
});
