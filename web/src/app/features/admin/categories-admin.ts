import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  OnInit,
  afterNextRender,
  inject,
  signal,
} from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError, errorMessage } from '../../core/api/api-error';
import { Category } from '../../core/api/models';
import { FieldError, applyFieldErrors, describedBy } from '../../shared/field-error';
import { StatusMessage } from '../../shared/status-message';

const NAME_MAX = 60;

export const CATEGORY_IN_USE_MESSAGE =
  'Cette catégorie est utilisée par des tickets : elle ne peut pas être supprimée.';
export const CATEGORY_DUPLICATE_MESSAGE = 'Une catégorie porte déjà ce nom.';

/** Categories tab (US-20): list, add, rename and delete. */
@Component({
  selector: 'app-categories-admin',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, FieldError, StatusMessage],
  template: `
    <section class="card space-y-4" aria-labelledby="categories-title">
      <h2 id="categories-title" tabindex="-1" class="text-xl font-semibold">Catégories</h2>

      <form [formGroup]="addForm" (ngSubmit)="add()" novalidate class="space-y-1">
        <label for="category-new" class="mb-1 block text-sm font-medium">
          Nouvelle catégorie
        </label>
        <div class="flex flex-wrap items-start gap-2">
          <div class="min-w-48 flex-1">
            <input
              id="category-new"
              type="text"
              class="field"
              autocomplete="off"
              [maxLength]="nameMax"
              formControlName="name"
              [attr.aria-describedby]="describe(addForm.controls.name, 'category-new-error')"
            />
            <app-field-error [control]="addForm.controls.name" errorId="category-new-error" />
          </div>
          <button type="submit" class="btn btn-accent" [disabled]="busy()">Ajouter</button>
        </div>
      </form>

      @if (actionError(); as message) {
        <p
          class="rounded-md border border-line bg-primary-soft p-3 text-sm font-medium"
          role="alert"
        >
          {{ message }}
        </p>
      }

      @switch (status()) {
        @case ('loading') {
          <app-status-message kind="loading" message="Chargement des catégories…" />
        }
        @case ('error') {
          <app-status-message kind="error" [message]="loadError()" (retry)="load()" />
        }
        @default {
          @if (categories().length === 0) {
            <app-status-message kind="empty" message="Aucune catégorie pour le moment." />
          } @else {
            <ul class="divide-y divide-line rounded-md border border-line">
              @for (category of categories(); track category.id) {
                <li class="flex flex-wrap items-center gap-2 p-3">
                  @if (editingId() === category.id) {
                    <form
                      class="flex flex-1 flex-wrap items-start gap-2"
                      [formGroup]="renameForm"
                      (ngSubmit)="rename(category)"
                      novalidate
                    >
                      <div class="min-w-48 flex-1">
                        <label class="sr-only" [for]="'category-name-' + category.id">
                          Nouveau nom de la catégorie {{ category.name }}
                        </label>
                        <input
                          type="text"
                          class="field"
                          autocomplete="off"
                          [id]="'category-name-' + category.id"
                          [maxLength]="nameMax"
                          formControlName="name"
                          [attr.aria-describedby]="
                            describe(renameForm.controls.name, 'category-rename-error')
                          "
                        />
                        <app-field-error
                          [control]="renameForm.controls.name"
                          errorId="category-rename-error"
                        />
                      </div>
                      <button type="submit" class="btn btn-secondary btn-small" [disabled]="busy()">
                        Enregistrer
                        <span class="sr-only">le nom de {{ category.name }}</span>
                      </button>
                      <button
                        type="button"
                        class="btn btn-secondary btn-small"
                        (click)="cancelEdit(category)"
                      >
                        Annuler
                      </button>
                    </form>
                  } @else {
                    <span class="flex-1 font-medium">{{ category.name }}</span>
                    @if (confirmId() === category.id) {
                      <span class="text-sm">Supprimer cette catégorie ?</span>
                      <button
                        type="button"
                        class="btn btn-secondary btn-small"
                        [disabled]="busy()"
                        [id]="'category-confirm-' + category.id"
                        (click)="remove(category)"
                      >
                        Confirmer
                        <span class="sr-only">la suppression de {{ category.name }}</span>
                      </button>
                      <button
                        type="button"
                        class="btn btn-secondary btn-small"
                        (click)="cancelDelete(category)"
                      >
                        Annuler
                      </button>
                    } @else {
                      <button
                        type="button"
                        class="btn btn-secondary btn-small"
                        [id]="'category-rename-' + category.id"
                        (click)="startEdit(category)"
                      >
                        Renommer
                        <span class="sr-only">{{ category.name }}</span>
                      </button>
                      <button
                        type="button"
                        class="btn btn-secondary btn-small"
                        [id]="'category-delete-' + category.id"
                        (click)="askDelete(category)"
                      >
                        Supprimer
                        <span class="sr-only">{{ category.name }}</span>
                      </button>
                    }
                  }
                </li>
              }
            </ul>
          }
        }
      }
    </section>
  `,
})
export class CategoriesAdmin implements OnInit {
  private readonly api = inject(ApiClient);
  private readonly fb = inject(NonNullableFormBuilder);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  protected readonly nameMax = NAME_MAX;
  protected readonly addForm = this.fb.group({
    name: ['', [Validators.required, Validators.maxLength(NAME_MAX)]],
  });
  protected readonly renameForm = this.fb.group({
    name: ['', [Validators.required, Validators.maxLength(NAME_MAX)]],
  });

  protected readonly categories = signal<Category[]>([]);
  protected readonly status = signal<'loading' | 'ready' | 'error'>('loading');
  protected readonly loadError = signal('');
  protected readonly actionError = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly editingId = signal<string | null>(null);
  protected readonly confirmId = signal<string | null>(null);

  ngOnInit(): void {
    void this.load();
  }

  protected describe(control: Parameters<typeof describedBy>[0], errorId: string): string | null {
    return describedBy(control, errorId);
  }

  async load(): Promise<void> {
    this.status.set('loading');
    try {
      this.categories.set(await firstValueFrom(this.api.listCategories()));
      this.status.set('ready');
    } catch (err) {
      this.loadError.set(errorMessage(err));
      this.status.set('error');
    }
  }

  protected async add(): Promise<void> {
    this.actionError.set(null);
    if (this.addForm.invalid) {
      this.addForm.markAllAsTouched();
      return;
    }
    const name = this.addForm.controls.name.value.trim();
    if (!name) {
      this.addForm.controls.name.setValue('');
      this.addForm.markAllAsTouched();
      return;
    }
    this.busy.set(true);
    try {
      const created = await firstValueFrom(this.api.createCategory(name));
      this.categories.update((list) => this.sorted([...list, created]));
      this.addForm.reset({ name: '' });
      this.focus('#category-new');
    } catch (err) {
      this.showWriteError(err, this.addForm);
    } finally {
      this.busy.set(false);
    }
  }

  protected startEdit(category: Category): void {
    this.confirmId.set(null);
    this.actionError.set(null);
    this.renameForm.reset({ name: category.name });
    this.editingId.set(category.id);
    this.focus(`#category-name-${category.id}`);
  }

  protected cancelEdit(category: Category): void {
    this.editingId.set(null);
    this.focus(`#category-rename-${category.id}`);
  }

  protected cancelDelete(category: Category): void {
    this.confirmId.set(null);
    this.focus(`#category-delete-${category.id}`);
  }

  /** Moves the keyboard focus once the next render has put the target in the page. */
  private focus(selector: string): void {
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>(selector)?.focus(), {
      injector: this.injector,
    });
  }

  protected async rename(category: Category): Promise<void> {
    this.actionError.set(null);
    const name = this.renameForm.controls.name.value.trim();
    if (this.renameForm.invalid || !name) {
      this.renameForm.markAllAsTouched();
      return;
    }
    if (name === category.name) {
      this.editingId.set(null);
      this.focus(`#category-rename-${category.id}`);
      return;
    }
    this.busy.set(true);
    try {
      const updated = await firstValueFrom(this.api.renameCategory(category.id, name));
      this.categories.update((list) =>
        this.sorted(list.map((c) => (c.id === updated.id ? updated : c))),
      );
      this.editingId.set(null);
      this.focus(`#category-rename-${category.id}`);
    } catch (err) {
      this.showWriteError(err, this.renameForm);
    } finally {
      this.busy.set(false);
    }
  }

  protected askDelete(category: Category): void {
    this.editingId.set(null);
    this.actionError.set(null);
    this.confirmId.set(category.id);
    this.focus(`#category-confirm-${category.id}`);
  }

  protected async remove(category: Category): Promise<void> {
    this.actionError.set(null);
    this.busy.set(true);
    try {
      await firstValueFrom(this.api.deleteCategory(category.id));
      this.categories.update((list) => list.filter((c) => c.id !== category.id));
      // The row is gone: the focus goes back to the section heading.
      this.focus('#categories-title');
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        this.actionError.set(CATEGORY_IN_USE_MESSAGE);
        this.focus(`#category-delete-${category.id}`);
      } else if (err instanceof ApiError && err.status === 404) {
        // Already deleted elsewhere: the list is simply out of date.
        this.categories.update((list) => list.filter((c) => c.id !== category.id));
        this.focus('#categories-title');
      } else {
        this.actionError.set(errorMessage(err));
      }
    } finally {
      this.confirmId.set(null);
      this.busy.set(false);
    }
  }

  private showWriteError(err: unknown, form: typeof this.addForm): void {
    if (err instanceof ApiError && err.status === 409) {
      this.actionError.set(CATEGORY_DUPLICATE_MESSAGE);
    } else if (!(err instanceof ApiError && err.status === 422 && applyFieldErrors(form, err))) {
      this.actionError.set(errorMessage(err));
    }
  }

  private sorted(list: Category[]): Category[] {
    return [...list].sort((a, b) => a.name.localeCompare(b.name, 'fr'));
  }
}
