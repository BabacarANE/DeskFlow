import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError } from '../../core/api/api-error';
import { article } from '../../testing/kb-fixtures';
import { KbEdit } from './kb-edit';

describe('KbEdit', () => {
  const api = {
    getArticle: vi.fn(),
    createArticle: vi.fn(),
    updateArticle: vi.fn(),
    publishArticle: vi.fn(),
  };
  let fixture: ComponentFixture<KbEdit>;
  let navigate: ReturnType<typeof vi.spyOn>;
  const el = () => fixture.nativeElement as HTMLElement;
  const field = (id: string) => el().querySelector(`#${id}`) as HTMLInputElement;
  const button = (text: string) =>
    Array.from(el().querySelectorAll('button')).find((b) => b.textContent?.includes(text));

  function type(id: string, value: string) {
    field(id).value = value;
    field(id).dispatchEvent(new Event('input'));
  }

  function fill(
    values: Partial<Record<'title' | 'symptom' | 'cause' | 'solution' | 'tags', string>>,
  ) {
    for (const [name, value] of Object.entries(values)) {
      type(name === 'title' ? 'article-title-input' : `article-${name}`, value);
    }
  }

  async function settle() {
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve));
    fixture.detectChanges();
  }

  async function render(id: string | null) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: ApiClient, useValue: api },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: new Map(id ? [['id', id]] : []) } },
        },
      ],
    });
    navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    fixture = TestBed.createComponent(KbEdit);
    fixture.detectChanges();
    await settle();
  }

  beforeEach(() => Object.values(api).forEach((fn) => fn.mockReset()));

  it('labels every field and offers a single accent button', async () => {
    await render(null);
    for (const id of [
      'article-title-input',
      'article-symptom',
      'article-cause',
      'article-solution',
      'article-tags',
    ]) {
      expect(el().querySelector(`label[for="${id}"]`), id).not.toBeNull();
    }
    expect(el().querySelectorAll('.btn-accent')).toHaveLength(1);
    expect(el().querySelector('h1')?.textContent).toContain('Nouvel article');
  });

  it('requires a title of 3 characters at least and links the error to the field', async () => {
    await render(null);
    fill({ title: 'ab' });
    (el().querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    await settle();
    expect(api.createArticle).not.toHaveBeenCalled();
    expect(el().textContent).toContain('au moins 3 caractères');
    const describedBy = field('article-title-input').getAttribute('aria-describedby');
    expect(describedBy).toBe('article-title-error');
    expect(el().querySelector(`#${describedBy}`)?.textContent).toContain('au moins 3');
  });

  it('refuses more than 10 tags or a tag longer than 30 characters', async () => {
    await render(null);
    fill({ title: 'Titre', tags: Array.from({ length: 11 }, (_, i) => `t${i}`).join(',') });
    (el().querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    await settle();
    expect(el().textContent).toContain('Au plus 10 étiquettes.');
    fill({ tags: 'x'.repeat(31) });
    (el().querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    await settle();
    expect(el().textContent).toContain('30 caractères au plus');
    expect(api.createArticle).not.toHaveBeenCalled();
  });

  it('creates a draft with parsed tags and opens it', async () => {
    api.createArticle.mockReturnValue(of(article('n1', { status: 'Draft', publishedAt: null })));
    await render(null);
    fill({
      title: ' Écran noir ',
      symptom: 'Plus d’image.',
      cause: '',
      solution: 'Changer le câble.',
      tags: 'écran, matériel, Écran',
    });
    (el().querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    await settle();
    expect(api.createArticle).toHaveBeenCalledWith({
      title: 'Écran noir',
      symptom: 'Plus d’image.',
      cause: '',
      solution: 'Changer le câble.',
      tags: ['écran', 'matériel'],
    });
    expect(api.publishArticle).not.toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith(['/kb', 'n1']);
  });

  it('saves then publishes with "Enregistrer et publier"', async () => {
    api.createArticle.mockReturnValue(of(article('n1', { status: 'Draft', publishedAt: null })));
    api.publishArticle.mockReturnValue(of(article('n1')));
    await render(null);
    fill({ title: 'Écran noir', symptom: 'Plus d’image.', solution: 'Changer le câble.' });
    button('Enregistrer et publier')?.click();
    await settle();
    expect(api.createArticle).toHaveBeenCalledTimes(1);
    expect(api.publishArticle).toHaveBeenCalledWith('n1');
    expect(navigate).toHaveBeenCalledWith(['/kb', 'n1']);
  });

  it('checks the symptom and solution before publishing, without any request', async () => {
    await render(null);
    fill({ title: 'Écran noir', symptom: '', solution: '' });
    button('Enregistrer et publier')?.click();
    await settle();
    expect(api.createArticle).not.toHaveBeenCalled();
    expect(el().textContent).toContain('Le symptôme est obligatoire pour publier.');
    expect(el().textContent).toContain('La solution est obligatoire pour publier.');
    expect(field('article-symptom').getAttribute('aria-describedby')).toContain(
      'article-symptom-error',
    );
  });

  it('keeps the saved draft and says so when the publication is refused', async () => {
    api.createArticle.mockReturnValue(of(article('n1', { status: 'Draft', publishedAt: null })));
    api.publishArticle.mockReturnValue(
      throwError(() => new ApiError(422, 'Certaines informations sont invalides.', null)),
    );
    api.updateArticle.mockReturnValue(of(article('n1', { status: 'Draft', publishedAt: null })));
    await render(null);
    fill({ title: 'Écran noir', symptom: 'S', solution: 'R' });
    button('Enregistrer et publier')?.click();
    await settle();
    expect(el().querySelector('[role="alert"]')?.textContent).toContain('enregistré en brouillon');
    expect(navigate).not.toHaveBeenCalled();

    // The next save updates the same article instead of creating a second one.
    (el().querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    await settle();
    expect(api.createArticle).toHaveBeenCalledTimes(1);
    expect(api.updateArticle).toHaveBeenCalledWith(
      'n1',
      expect.objectContaining({ title: 'Écran noir' }),
    );
  });

  it('shows 422 errors under the matching fields', async () => {
    api.createArticle.mockReturnValue(
      throwError(
        () =>
          new ApiError(422, 'Certaines informations sont invalides.', null, {
            title: ['Titre déjà utilisé.'],
            tags: ['Étiquette invalide.'],
          }),
      ),
    );
    await render(null);
    fill({ title: 'Titre valide' });
    (el().querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    await settle();
    expect(field('article-title-input').parentElement?.textContent).toContain(
      'Titre déjà utilisé.',
    );
    expect(field('article-tags').parentElement?.textContent).toContain('Étiquette invalide.');
    expect(navigate).not.toHaveBeenCalled();
  });

  it('loads an article to edit, updates it and does not offer to publish a published one', async () => {
    api.getArticle.mockReturnValue(of(article('a1')));
    api.updateArticle.mockReturnValue(of(article('a1')));
    await render('a1');
    expect(api.getArticle).toHaveBeenCalledWith('a1');
    expect(field('article-title-input').value).toBe('Article a1');
    expect(field('article-tags').value).toBe('vpn, accès');
    expect(button('Enregistrer et publier')).toBeUndefined();

    fill({ title: 'Titre modifié' });
    (el().querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    await settle();
    expect(api.updateArticle).toHaveBeenCalledWith(
      'a1',
      expect.objectContaining({ title: 'Titre modifié', tags: ['vpn', 'accès'] }),
    );
    expect(navigate).toHaveBeenCalledWith(['/kb', 'a1']);
  });

  it('shows the 404 screen for an unknown article', async () => {
    api.getArticle.mockReturnValue(
      throwError(() => new ApiError(404, 'Élément introuvable.', null)),
    );
    await render('zzz');
    expect(el().textContent).toContain('Page introuvable');
    expect(el().querySelector('form')).toBeNull();
  });
});
