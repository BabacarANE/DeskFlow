import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';
import { Me, Role } from './core/api/models';
import { AuthService } from './core/auth/auth.service';
import { HUB_FACTORY } from './core/realtime/hub';

function me(role: Role): Me {
  return {
    id: 'u1',
    email: 'u@acme.test',
    displayName: 'Utilisateur',
    role,
    isDemo: false,
    tenant:
      role === 'SuperAdmin' ? null : { id: 't1', name: 'Acme', slug: 'acme', theme: null as never },
  };
}

describe('main navigation', () => {
  const user = signal<Me | null>(null);

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: HUB_FACTORY,
          useValue: () => ({
            start: () => new Promise<void>(() => undefined),
            stop: () => Promise.resolve(),
            on: () => undefined,
            onclose: () => undefined,
          }),
        },
        {
          provide: AuthService,
          useValue: {
            user,
            role: () => user()?.role ?? null,
            isDemo: () => false,
            logout: vi.fn(),
          },
        },
      ],
    }).compileComponents();
  });

  function links(role: Role): string[] {
    user.set(me(role));
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    return Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('nav a')).map(
      (a) => a.textContent?.trim() ?? '',
    );
  }

  it('offers the knowledge base to every role that has a company', () => {
    for (const role of ['Requester', 'Agent', 'TenantAdmin'] as const) {
      expect(links(role), role).toContain('Base de connaissances');
    }
  });

  it('does not offer it to the SuperAdmin, who has no company', () => {
    expect(links('SuperAdmin')).not.toContain('Base de connaissances');
  });

  it('points the entry to /kb', () => {
    user.set(me('Requester'));
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const link = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('nav a')).find(
      (a) => a.textContent?.includes('Base de connaissances'),
    );
    expect(link?.getAttribute('href')).toBe('/kb');
  });
});
