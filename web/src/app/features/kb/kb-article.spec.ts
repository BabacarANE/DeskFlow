import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError } from '../../core/api/api-error';
import { Role } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { article } from '../../testing/kb-fixtures';
import { KbArticle } from './kb-article';

describe('KbArticle', () => {
  const api = { getArticle: vi.fn(), publishArticle: vi.fn(), unpublishArticle: vi.fn() };
  const role = signal<Role | null>('Agent');
  let fixture: ComponentFixture<KbArticle>;
  const el = () => fixture.nativeElement as HTMLElement;
  const button = (text: string) =>
    Array.from(el().querySelectorAll('button, a.btn')).find((b) =>
      b.textContent?.trim().startsWith(text),
    ) as HTMLElement | undefined;

  async function render() {
    fixture = TestBed.createComponent(KbArticle);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  beforeEach(() => {
    Object.values(api).forEach((fn) => fn.mockReset());
    role.set('Agent');
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: ApiClient, useValue: api },
        { provide: AuthService, useValue: { role } },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['id', 'a1']]) } } },
      ],
    });
  });

  it('reads the Symptôme, Cause and Solution sections and the tags', async () => {
    api.getArticle.mockReturnValue(of(article('a1')));
    await render();
    expect(api.getArticle).toHaveBeenCalledWith('a1');
    const headings = Array.from(el().querySelectorAll('h2')).map((h) => h.textContent?.trim());
    expect(headings).toEqual(['Symptôme', 'Cause', 'Solution']);
    expect(el().querySelector('h1')?.textContent).toContain('Article a1');
    expect(el().textContent).toContain('Renouvelez le mot de passe.');
    expect(el().querySelector('[aria-label="Étiquettes"]')?.textContent).toContain('vpn');
  });

  it('gives a requester no edit, publish or unpublish button', async () => {
    role.set('Requester');
    api.getArticle.mockReturnValue(of(article('a1')));
    await render();
    expect(button('Modifier')).toBeUndefined();
    expect(button('Publier')).toBeUndefined();
    expect(button('Dépublier')).toBeUndefined();
    expect(el().querySelector('.btn')).toBeNull();
  });

  it('gives an agent Modifier and a single accent Publier on a draft, with its source ticket', async () => {
    api.getArticle.mockReturnValue(
      of(article('a1', { status: 'Draft', publishedAt: null, sourceTicketId: 't9' })),
    );
    await render();
    expect(button('Modifier')?.getAttribute('href')).toBe('/kb/a1/edit');
    expect(el().querySelectorAll('.btn-accent')).toHaveLength(1);
    expect(button('Publier')).toBeDefined();
    expect(button('Dépublier')).toBeUndefined();
    expect(el().querySelector('.badge')?.textContent).toContain('Brouillon');
    expect(el().querySelector('a[href="/tickets/t9"]')).not.toBeNull();
  });

  it('publishes a draft and shows the new state', async () => {
    api.getArticle.mockReturnValue(of(article('a1', { status: 'Draft', publishedAt: null })));
    api.publishArticle.mockReturnValue(of(article('a1')));
    await render();
    button('Publier')?.click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(api.publishArticle).toHaveBeenCalledWith('a1');
    expect(el().querySelector('.badge')?.textContent).toContain('Publié');
    expect(button('Dépublier')).toBeDefined();
  });

  it('explains a refused publication (422)', async () => {
    api.getArticle.mockReturnValue(of(article('a1', { status: 'Draft', publishedAt: null })));
    api.publishArticle.mockReturnValue(
      throwError(() => new ApiError(422, 'Certaines informations sont invalides.', null)),
    );
    await render();
    button('Publier')?.click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(el().querySelector('[role="alert"]')?.textContent).toContain('symptôme');
    expect(el().querySelector('.badge')?.textContent).toContain('Brouillon');
  });

  it('asks for a confirmation before unpublishing', async () => {
    api.getArticle.mockReturnValue(of(article('a1')));
    api.unpublishArticle.mockReturnValue(of(article('a1', { status: 'Draft', publishedAt: null })));
    await render();
    button('Dépublier')?.click();
    fixture.detectChanges();
    expect(api.unpublishArticle).not.toHaveBeenCalled();
    expect(button('Confirmer la dépublication')).toBeDefined();

    button('Annuler')?.click();
    fixture.detectChanges();
    expect(button('Confirmer la dépublication')).toBeUndefined();

    button('Dépublier')?.click();
    fixture.detectChanges();
    button('Confirmer la dépublication')?.click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(api.unpublishArticle).toHaveBeenCalledWith('a1');
    expect(el().querySelector('.badge')?.textContent).toContain('Brouillon');
  });

  it('shows the 404 screen when the article does not exist (or is a draft for a requester)', async () => {
    role.set('Requester');
    api.getArticle.mockReturnValue(
      throwError(() => new ApiError(404, 'Élément introuvable.', null)),
    );
    await render();
    expect(el().textContent).toContain('Page introuvable');
    expect(el().querySelector('h2')).toBeNull();
  });

  it('shows an error with a retry button for other failures', async () => {
    api.getArticle.mockReturnValueOnce(
      throwError(() => new ApiError(500, 'Le serveur a rencontré une erreur.', null)),
    );
    await render();
    expect(el().querySelector('[role="alert"]')?.textContent).toContain('serveur');
    api.getArticle.mockReturnValue(of(article('a1')));
    (el().querySelector('[role="alert"] button') as HTMLButtonElement).click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(el().querySelector('h1')).not.toBeNull();
  });
});
