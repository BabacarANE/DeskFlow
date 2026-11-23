import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { App } from './app';
import { Me } from './core/api/models';
import { AuthService } from './core/auth/auth.service';
import { ThemeService } from './core/theme/theme.service';
import { ApiClient } from './core/api/api-client';
import { TicketList } from './features/tickets/ticket-list';

const DARK_THEME = { colorPrimary: '#101820', colorSurface: '#FAFAFA', colorAccent: '#FFD400' };

const ME: Me = {
  id: 'u1',
  email: 'admin@acme.test',
  displayName: 'Alice',
  role: 'TenantAdmin',
  isDemo: false,
  tenant: { id: 't1', name: 'Acme', slug: 'acme', theme: DARK_THEME },
};

describe('App', () => {
  const user = signal<Me | null>(null);

  beforeEach(async () => {
    user.set(null);
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideRouter([{ path: 'tickets', component: TicketList }]),
        {
          provide: ApiClient,
          useValue: {
            listTickets: () => of({ items: [], total: 0, page: 1, pageSize: 20 }),
            listCategories: () => of([]),
            listAssignees: () => of([]),
          },
        },
        provideHttpClient(),
        provideHttpClientTesting(),
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

  afterEach(() => TestBed.inject(ThemeService).reset());

  it('should create the app', () => {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    expect(app).toBeTruthy();
  });

  it('renders the h1 of the current page, and only that one', async () => {
    user.set(ME);
    const fixture = TestBed.createComponent(App);
    await TestBed.inject(Router).navigateByUrl('/tickets');
    await fixture.whenStable();
    const titles = (fixture.nativeElement as HTMLElement).querySelectorAll('h1');
    expect(titles).toHaveLength(1);
    expect(titles[0].textContent).toContain('Tickets');
  });

  it('keeps the navigation readable with a dark primary colour', async () => {
    user.set(ME);
    TestBed.inject(ThemeService).apply(DARK_THEME);
    const fixture = TestBed.createComponent(App);
    await TestBed.inject(Router).navigateByUrl('/tickets');
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    // A dark primary needs white text...
    expect(document.documentElement.style.getPropertyValue('--on-primary')).toBe('var(--on-light)');
    // ...so no navigation state may paint a light background behind it.
    const links = Array.from(el.querySelectorAll('nav a'));
    expect(links.length).toBeGreaterThan(0);
    for (const link of links) {
      expect(link.className).not.toMatch(/\bbg-|hover:bg-/);
    }
    const active = el.querySelector('nav a[aria-current="page"]');
    expect(active?.className).toContain('border-current');
  });
});
