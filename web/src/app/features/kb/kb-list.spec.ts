import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError } from '../../core/api/api-error';
import { Role } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { articlePage, articleSummary } from '../../testing/kb-fixtures';
import { KbList } from './kb-list';

describe('KbList', () => {
  const api = { listArticles: vi.fn() };
  const role = signal<Role | null>('Requester');
  let fixture: ComponentFixture<KbList>;
  const el = () => fixture.nativeElement as HTMLElement;

  async function render() {
    // NgModel registers its controls in a microtask: wait before typing.

    fixture = TestBed.createComponent(KbList);
    fixture.detectChanges();
    await fixture.whenStable();
    // toObservable runs through an effect: flush it.
    fixture.detectChanges();
    fixture.detectChanges();
  }

  function type(text: string) {
    const input = el().querySelector('#kb-search') as HTMLInputElement;
    input.value = text;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  const lastQuery = () => api.listArticles.mock.calls.at(-1)?.[0];

  beforeEach(() => {
    vi.useFakeTimers();
    api.listArticles.mockReset();
    api.listArticles.mockReturnValue(of(articlePage([articleSummary('a1')])));
    role.set('Requester');
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: ApiClient, useValue: api },
        { provide: AuthService, useValue: { role } },
      ],
    });
  });

  afterEach(() => vi.useRealTimers());

  it('lists articles with a loading state first', async () => {
    const pending = new Subject<ReturnType<typeof articlePage>>();
    api.listArticles.mockReturnValue(pending);
    await render();
    expect(el().querySelector('app-status-message [role="status"]')?.textContent).toContain(
      'Chargement',
    );
    pending.next(articlePage([articleSummary('a1', { title: 'Le VPN refuse' })]));
    fixture.detectChanges();
    expect(el().querySelector('h2 a')?.textContent).toContain('Le VPN refuse');
    expect(el().textContent).not.toContain('Chargement');
  });

  it('labels the search field and links each article to its page', async () => {
    await render();
    expect(el().querySelector('label[for="kb-search"]')).not.toBeNull();
    expect(el().querySelector('h2 a')?.getAttribute('href')).toBe('/kb/a1');
  });

  it('searches 300 ms after the last keystroke, once, from page 1', async () => {
    await render();
    expect(api.listArticles).toHaveBeenCalledTimes(1);
    for (const text of ['v', 'vp', 'vpn']) {
      type(text);
      vi.advanceTimersByTime(100);
    }
    expect(api.listArticles).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(200);
    fixture.detectChanges();
    expect(api.listArticles).toHaveBeenCalledTimes(2);
    expect(lastQuery()).toEqual({ q: 'vpn', status: undefined, page: 1, pageSize: 10 });
  });

  it('drops the answer of an obsolete search', async () => {
    const slow = new Subject<ReturnType<typeof articlePage>>();
    await render();
    api.listArticles.mockReturnValueOnce(slow);
    type('vpn');
    vi.advanceTimersByTime(300);
    fixture.detectChanges();
    api.listArticles.mockReturnValueOnce(
      of(articlePage([articleSummary('b', { title: 'Récent' })])),
    );
    type('wifi');
    vi.advanceTimersByTime(300);
    fixture.detectChanges();
    slow.next(articlePage([articleSummary('a', { title: 'Obsolète' })]));
    fixture.detectChanges();
    expect(el().textContent).toContain('Récent');
    expect(el().textContent).not.toContain('Obsolète');
  });

  it('searches at once when the form is submitted', async () => {
    await render();
    type('imprimante');
    el().querySelector('form')?.dispatchEvent(new Event('submit'));
    fixture.detectChanges();
    expect(lastQuery().q).toBe('imprimante');
  });

  it('announces the number of results in a polite live region', async () => {
    api.listArticles.mockReturnValue(of(articlePage([articleSummary('a1')], 12)));
    await render();
    const live = el().querySelector('p[aria-live="polite"]');
    expect(live?.textContent).toContain('12 articles trouvés.');
  });

  it('shows the empty state, with the searched words', async () => {
    api.listArticles.mockReturnValue(of(articlePage([])));
    await render();
    expect(el().textContent).toContain('Aucun article pour le moment.');
    type('zzz');
    vi.advanceTimersByTime(300);
    fixture.detectChanges();
    fixture.detectChanges();
    expect(el().textContent).toContain('Aucun article ne correspond à « zzz ».');
  });

  it('shows the error state and retries', async () => {
    api.listArticles.mockReturnValueOnce(
      throwError(() => new ApiError(500, 'Le serveur a rencontré une erreur.', null)),
    );
    await render();
    expect(el().querySelector('[role="alert"]')?.textContent).toContain('serveur');
    api.listArticles.mockReturnValue(of(articlePage([articleSummary('a1')])));
    (el().querySelector('[role="alert"] button') as HTMLButtonElement).click();
    fixture.detectChanges();
    fixture.detectChanges();
    expect(el().querySelector('[role="alert"]')).toBeNull();
    expect(el().querySelector('h2 a')).not.toBeNull();
  });

  it('paginates', async () => {
    api.listArticles.mockReturnValue(of(articlePage([articleSummary('a1')], 25)));
    await render();
    const next = Array.from(el().querySelectorAll('button')).find((b) =>
      b.textContent?.includes('Suivant'),
    ) as HTMLButtonElement;
    next.click();
    fixture.detectChanges();
    expect(lastQuery().page).toBe(2);
  });

  it('hides the status filter, the new-article button and drafts from a requester', async () => {
    await render();
    expect(el().querySelector('#kb-status')).toBeNull();
    expect(el().textContent).not.toContain('Nouvel article');
    expect(lastQuery().status).toBeUndefined();
  });

  it('gives an agent the status filter, a Draft badge and the new-article button', async () => {
    role.set('Agent');
    api.listArticles.mockReturnValue(
      of(articlePage([articleSummary('d1', { status: 'Draft', publishedAt: null })])),
    );
    await render();
    expect(el().querySelector('label[for="kb-status"]')).not.toBeNull();
    expect(el().querySelectorAll('.btn-accent')).toHaveLength(1);
    expect(el().querySelector('.btn-accent')?.getAttribute('href')).toBe('/kb/new');
    expect(el().querySelector('.badge')?.textContent).toContain('Brouillon');

    const select = el().querySelector('#kb-status') as HTMLSelectElement;
    select.value = 'Draft';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(lastQuery().status).toBe('Draft');
    expect(lastQuery().page).toBe(1);
  });
});
