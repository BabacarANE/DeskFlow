import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError } from '../../core/api/api-error';
import { ArticleSummary, Page } from '../../core/api/models';
import { articlePage, articleSummary } from '../../testing/kb-fixtures';
import { ArticleSuggestions } from './article-suggestions';

describe('ArticleSuggestions', () => {
  const api = { listArticles: vi.fn() };
  let fixture: ComponentFixture<ArticleSuggestions>;
  const el = () => fixture.nativeElement as HTMLElement;

  function type(text: string) {
    fixture.componentRef.setInput('term', text);
    fixture.detectChanges();
  }

  beforeEach(() => {
    vi.useFakeTimers();
    api.listArticles.mockReset();
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: ApiClient, useValue: api }],
    });
    fixture = TestBed.createComponent(ArticleSuggestions);
    fixture.detectChanges();
  });

  afterEach(() => vi.useRealTimers());

  it('asks nothing for fewer than 3 characters', () => {
    type('vp');
    vi.advanceTimersByTime(1000);
    expect(api.listArticles).not.toHaveBeenCalled();
  });

  it('waits 400 ms without typing, then asks for 5 articles at most', () => {
    api.listArticles.mockReturnValue(of(articlePage([articleSummary('a1')])));
    type('vpn');
    vi.advanceTimersByTime(399);
    expect(api.listArticles).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1);
    expect(api.listArticles).toHaveBeenCalledTimes(1);
    expect(api.listArticles).toHaveBeenCalledWith({ q: 'vpn', pageSize: 5 });
  });

  it('sends one request for a burst of keystrokes', () => {
    api.listArticles.mockReturnValue(of(articlePage([])));
    for (const text of ['vpn', 'vpn r', 'vpn ré', 'vpn réseau']) {
      type(text);
      vi.advanceTimersByTime(100);
    }
    vi.advanceTimersByTime(400);
    expect(api.listArticles).toHaveBeenCalledTimes(1);
    expect(api.listArticles).toHaveBeenCalledWith({ q: 'vpn réseau', pageSize: 5 });
  });

  it('lists links that open the article in a new tab, in a polite live region', () => {
    api.listArticles.mockReturnValue(
      of(articlePage([articleSummary('a1', { title: 'Le VPN refuse la connexion' })])),
    );
    type('vpn');
    vi.advanceTimersByTime(400);
    fixture.detectChanges();
    expect(el().querySelector('[aria-live="polite"]')).not.toBeNull();
    const link = el().querySelector('a') as HTMLAnchorElement;
    expect(link.textContent).toContain('Le VPN refuse la connexion');
    expect(link.getAttribute('href')).toBe('/kb/a1');
    expect(link.getAttribute('target')).toBe('_blank');
    expect(link.getAttribute('rel')).toContain('noopener');
    expect(el().textContent).toContain('1 article suggéré.');
  });

  it('shows nothing when there is no suggestion, and clears old ones', () => {
    api.listArticles.mockReturnValueOnce(of(articlePage([articleSummary('a1')])));
    type('vpn');
    vi.advanceTimersByTime(400);
    fixture.detectChanges();
    expect(el().querySelector('a')).not.toBeNull();

    api.listArticles.mockReturnValueOnce(of(articlePage([])));
    type('imprimante');
    vi.advanceTimersByTime(400);
    fixture.detectChanges();
    expect(el().querySelector('a')).toBeNull();
    expect(el().querySelector('ul')).toBeNull();

    type('im');
    vi.advanceTimersByTime(400);
    fixture.detectChanges();
    expect(el().querySelector('ul')).toBeNull();
  });

  it('cancels the obsolete request: a late answer never replaces the newest', () => {
    const first = new Subject<Page<ArticleSummary>>();
    const second = new Subject<Page<ArticleSummary>>();
    api.listArticles.mockReturnValueOnce(first).mockReturnValueOnce(second);
    type('vpn');
    vi.advanceTimersByTime(400);
    type('imprimante');
    vi.advanceTimersByTime(400);
    expect(first.observed).toBe(false);

    second.next(articlePage([articleSummary('new', { title: 'Imprimante hors ligne' })]));
    first.next(articlePage([articleSummary('old', { title: 'Ancien résultat' })]));
    fixture.detectChanges();
    expect(el().textContent).toContain('Imprimante hors ligne');
    expect(el().textContent).not.toContain('Ancien résultat');
  });

  it('fails silently: no message, no alert, and the next term still works', () => {
    api.listArticles.mockReturnValueOnce(
      throwError(() => new ApiError(500, 'Le serveur a rencontré une erreur.', null)),
    );
    type('vpn');
    vi.advanceTimersByTime(400);
    fixture.detectChanges();
    expect(el().querySelector('[role="alert"]')).toBeNull();
    expect(el().querySelector('a')).toBeNull();

    api.listArticles.mockReturnValueOnce(of(articlePage([articleSummary('a2')])));
    type('wifi');
    vi.advanceTimersByTime(400);
    fixture.detectChanges();
    expect(el().querySelector('a')).not.toBeNull();
  });

  it('survives a client that throws synchronously', () => {
    api.listArticles.mockImplementation(() => {
      throw new Error('boom');
    });
    type('vpn');
    expect(() => vi.advanceTimersByTime(400)).not.toThrow();
    fixture.detectChanges();
    expect(el().querySelector('a')).toBeNull();
  });
});
