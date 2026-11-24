import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError } from '../../core/api/api-error';
import { articlePage, articleSummary } from '../../testing/kb-fixtures';
import { detail } from '../../testing/ticket-fixtures';
import { TicketNew } from './ticket-new';

/** Suggestions under the title of /tickets/new (US-18). */
describe('TicketNew suggestions', () => {
  const api = { createTicket: vi.fn(), listCategories: vi.fn(), listArticles: vi.fn() };
  let fixture: ComponentFixture<TicketNew>;
  const el = () => fixture.nativeElement as HTMLElement;

  async function typeTitle(value: string) {
    const input = el().querySelector('#ticket-title') as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    // Real timers: wait past the 400 ms pause.
    await new Promise((resolve) => setTimeout(resolve, 480));
    await fixture.whenStable();
    fixture.detectChanges();
  }

  beforeEach(async () => {
    Object.values(api).forEach((fn) => fn.mockReset());
    api.listCategories.mockReturnValue(of([]));
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: ApiClient, useValue: api }],
    });
    vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    fixture = TestBed.createComponent(TicketNew);
    fixture.detectChanges();
    await fixture.whenStable();
  });

  it('shows article links under the title field once the user pauses', async () => {
    api.listArticles.mockReturnValue(
      of(articlePage([articleSummary('a1', { title: 'Le VPN refuse la connexion' })])),
    );
    await typeTitle('Mon VPN ne marche pas');
    expect(api.listArticles).toHaveBeenCalledWith({ q: 'Mon VPN ne marche pas', pageSize: 5 });
    const titleField = (el().querySelector('#ticket-title') as HTMLElement).parentElement;
    expect(titleField?.textContent).toContain('Le VPN refuse la connexion');
  });

  it('never blocks the ticket creation when the suggestions fail', async () => {
    api.listArticles.mockReturnValue(
      throwError(() => new ApiError(500, 'Le serveur a rencontré une erreur.', null)),
    );
    api.createTicket.mockReturnValue(of(detail('new1')));
    await typeTitle('Mon VPN ne marche pas');
    expect(el().querySelector('[role="alert"]')).toBeNull();

    const description = el().querySelector('#ticket-description') as HTMLTextAreaElement;
    description.value = 'Depuis ce matin.';
    description.dispatchEvent(new Event('input'));
    el().querySelector('form')?.dispatchEvent(new Event('submit'));
    await fixture.whenStable();
    expect(api.createTicket).toHaveBeenCalledTimes(1);
  });
});
