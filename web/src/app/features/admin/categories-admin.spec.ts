import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError } from '../../core/api/api-error';
import { Category } from '../../core/api/models';
import {
  CATEGORY_DUPLICATE_MESSAGE,
  CATEGORY_IN_USE_MESSAGE,
  CategoriesAdmin,
} from './categories-admin';

const CATEGORIES: Category[] = [
  { id: 'c1', name: 'Accès et comptes' },
  { id: 'c2', name: 'Matériel' },
];

describe('CategoriesAdmin', () => {
  const api = {
    listCategories: vi.fn(),
    createCategory: vi.fn(),
    renameCategory: vi.fn(),
    deleteCategory: vi.fn(),
  };
  let fixture: ComponentFixture<CategoriesAdmin>;
  const el = () => fixture.nativeElement as HTMLElement;
  const names = () =>
    Array.from(el().querySelectorAll('li > span.font-medium')).map((s) => s.textContent?.trim());
  const button = (text: string, within: ParentNode = el()) =>
    Array.from(within.querySelectorAll('button')).find((b) =>
      b.textContent?.trim().startsWith(text),
    ) as HTMLButtonElement | undefined;
  const row = (index: number) => el().querySelectorAll('ul > li')[index] as HTMLElement;

  async function settle() {
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve));
    fixture.detectChanges();
  }

  function typeIn(selector: string, value: string) {
    const input = el().querySelector(selector) as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  async function render() {
    fixture = TestBed.createComponent(CategoriesAdmin);
    fixture.detectChanges();
    await settle();
  }

  beforeEach(() => {
    Object.values(api).forEach((fn) => fn.mockReset());
    api.listCategories.mockReturnValue(of(CATEGORIES));
    TestBed.configureTestingModule({ providers: [{ provide: ApiClient, useValue: api }] });
  });

  it('lists the categories, with a labelled add field', async () => {
    await render();
    expect(names()).toEqual(['Accès et comptes', 'Matériel']);
    expect(el().querySelector('label[for="category-new"]')).not.toBeNull();
  });

  it('shows the empty and error states', async () => {
    api.listCategories.mockReturnValueOnce(of([]));
    await render();
    expect(el().textContent).toContain('Aucune catégorie pour le moment.');

    api.listCategories.mockReturnValueOnce(
      throwError(() => new ApiError(500, 'Le serveur a rencontré une erreur.', null)),
    );
    await render();
    expect(el().querySelector('[role="alert"]')?.textContent).toContain('serveur');
    api.listCategories.mockReturnValue(of(CATEGORIES));
    (el().querySelector('[role="alert"] button') as HTMLButtonElement).click();
    await settle();
    expect(names()).toHaveLength(2);
  });

  it('adds a category and keeps the list sorted', async () => {
    api.createCategory.mockReturnValue(of({ id: 'c3', name: 'Logiciels' }));
    await render();
    typeIn('#category-new', '  Logiciels ');
    button('Ajouter')?.click();
    await settle();
    expect(api.createCategory).toHaveBeenCalledWith('Logiciels');
    expect(names()).toEqual(['Accès et comptes', 'Logiciels', 'Matériel']);
    expect((el().querySelector('#category-new') as HTMLInputElement).value).toBe('');
  });

  it('refuses an empty name without calling the API', async () => {
    await render();
    button('Ajouter')?.click();
    await settle();
    expect(api.createCategory).not.toHaveBeenCalled();
    expect(el().textContent).toContain('Ce champ est obligatoire.');
    expect(el().querySelector('#category-new')?.getAttribute('aria-describedby')).toBe(
      'category-new-error',
    );
  });

  it('explains a duplicate name (409) and 422 field errors', async () => {
    api.createCategory.mockReturnValueOnce(throwError(() => new ApiError(409, 'Conflit', null)));
    await render();
    typeIn('#category-new', 'Matériel');
    button('Ajouter')?.click();
    await settle();
    expect(el().querySelector('[role="alert"]')?.textContent).toContain(CATEGORY_DUPLICATE_MESSAGE);

    api.createCategory.mockReturnValueOnce(
      throwError(
        () =>
          new ApiError(422, 'Certaines informations sont invalides.', null, {
            name: ['Nom trop long.'],
          }),
      ),
    );
    button('Ajouter')?.click();
    await settle();
    expect(el().querySelector('#category-new')?.parentElement?.textContent).toContain(
      'Nom trop long.',
    );
  });

  it('renames a category', async () => {
    api.renameCategory.mockReturnValue(of({ id: 'c2', name: 'Équipement' }));
    await render();
    button('Renommer', row(1))?.click();
    fixture.detectChanges();
    typeIn('#category-name-c2', 'Équipement');
    button('Enregistrer', row(1))?.click();
    await settle();
    expect(api.renameCategory).toHaveBeenCalledWith('c2', 'Équipement');
    expect(names()).toEqual(['Accès et comptes', 'Équipement']);
  });

  it('reports a duplicate name when renaming and lets the user cancel', async () => {
    api.renameCategory.mockReturnValue(throwError(() => new ApiError(409, 'Conflit', null)));
    await render();
    button('Renommer', row(1))?.click();
    fixture.detectChanges();
    typeIn('#category-name-c2', 'Accès et comptes');
    button('Enregistrer', row(1))?.click();
    await settle();
    expect(el().querySelector('[role="alert"]')?.textContent).toContain(CATEGORY_DUPLICATE_MESSAGE);
    button('Annuler', row(1))?.click();
    fixture.detectChanges();
    expect(names()).toEqual(['Accès et comptes', 'Matériel']);
  });

  it('deletes only after a confirmation', async () => {
    api.deleteCategory.mockReturnValue(of(undefined));
    await render();
    button('Supprimer', row(0))?.click();
    fixture.detectChanges();
    expect(api.deleteCategory).not.toHaveBeenCalled();
    expect(el().textContent).toContain('Supprimer cette catégorie ?');
    button('Confirmer', row(0))?.click();
    await settle();
    expect(api.deleteCategory).toHaveBeenCalledWith('c1');
    expect(names()).toEqual(['Matériel']);
  });

  it('tells that a category used by tickets cannot be deleted (409) and keeps it', async () => {
    api.deleteCategory.mockReturnValue(throwError(() => new ApiError(409, 'Conflit', null)));
    await render();
    button('Supprimer', row(1))?.click();
    fixture.detectChanges();
    button('Confirmer', row(1))?.click();
    await settle();
    expect(el().querySelector('[role="alert"]')?.textContent).toContain(CATEGORY_IN_USE_MESSAGE);
    expect(names()).toEqual(['Accès et comptes', 'Matériel']);
  });
});
