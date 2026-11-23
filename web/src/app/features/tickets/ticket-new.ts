import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError, errorMessage } from '../../core/api/api-error';
import { PRIORITIES, PRIORITY_LABELS } from '../../core/api/labels';
import { Category, CreateTicketRequest, TicketPriority } from '../../core/api/models';
import { FieldError, applyFieldErrors } from '../../shared/field-error';

@Component({
  selector: 'app-ticket-new',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, RouterLink, FieldError],
  template: `
    <section class="space-y-6" aria-labelledby="new-ticket-title">
      <div>
        <a routerLink="/tickets" class="text-sm">← Retour aux tickets</a>
        <h1 id="new-ticket-title" class="mt-2 text-2xl font-semibold">Nouveau ticket</h1>
      </div>

      <form class="card space-y-5" [formGroup]="form" (ngSubmit)="submit()" novalidate>
        <div>
          <label for="ticket-title" class="mb-1 block text-sm font-medium">Titre</label>
          <input
            id="ticket-title"
            type="text"
            class="field"
            maxlength="200"
            autocomplete="off"
            formControlName="title"
          />
          <app-field-error [control]="form.controls.title" />
        </div>

        <div>
          <label for="ticket-description" class="mb-1 block text-sm font-medium">
            Description
          </label>
          <textarea
            id="ticket-description"
            class="field"
            rows="8"
            maxlength="5000"
            formControlName="description"
          ></textarea>
          <p class="mt-1 text-sm text-ink-muted">
            Décrivez le problème : ce que vous faisiez, ce qui s’est passé, depuis quand.
          </p>
          <app-field-error [control]="form.controls.description" />
        </div>

        <div class="grid gap-5 sm:grid-cols-2">
          <div>
            <label for="ticket-category" class="mb-1 block text-sm font-medium">Catégorie</label>
            <select id="ticket-category" class="field" formControlName="categoryId">
              <option value="">Aucune catégorie</option>
              @for (category of categories(); track category.id) {
                <option [value]="category.id">{{ category.name }}</option>
              }
            </select>
            <app-field-error [control]="form.controls.categoryId" />
          </div>
          <div>
            <label for="ticket-priority" class="mb-1 block text-sm font-medium">Priorité</label>
            <select id="ticket-priority" class="field" formControlName="priority">
              @for (priority of priorities; track priority) {
                <option [value]="priority">{{ priorityLabels[priority] }}</option>
              }
            </select>
            <app-field-error [control]="form.controls.priority" />
          </div>
        </div>

        @if (formError(); as message) {
          <p
            class="rounded-md border border-line bg-primary-soft p-3 text-sm font-medium"
            role="alert"
          >
            {{ message }}
          </p>
        }

        <div class="flex flex-wrap gap-3">
          <button type="submit" class="btn btn-accent" [disabled]="busy()">
            {{ busy() ? 'Création…' : 'Créer le ticket' }}
          </button>
          <a routerLink="/tickets" class="btn btn-secondary">Annuler</a>
        </div>
      </form>
    </section>
  `,
})
export class TicketNew implements OnInit {
  private readonly api = inject(ApiClient);
  private readonly router = inject(Router);

  protected readonly priorities = PRIORITIES;
  protected readonly priorityLabels = PRIORITY_LABELS;

  protected readonly form = inject(NonNullableFormBuilder).group({
    title: ['', [Validators.required, Validators.maxLength(200)]],
    description: ['', [Validators.required, Validators.maxLength(5000)]],
    categoryId: [''],
    priority: ['Normal' as TicketPriority, Validators.required],
  });

  protected readonly categories = signal<Category[]>([]);
  protected readonly busy = signal(false);
  protected readonly formError = signal<string | null>(null);

  ngOnInit(): void {
    void this.loadCategories();
  }

  protected async submit(): Promise<void> {
    this.formError.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const value = this.form.getRawValue();
    const body: CreateTicketRequest = {
      title: value.title.trim(),
      description: value.description.trim(),
      categoryId: value.categoryId || null,
      priority: value.priority,
    };
    this.busy.set(true);
    try {
      const ticket = await firstValueFrom(this.api.createTicket(body));
      await this.router.navigate(['/tickets', ticket.id]);
    } catch (err) {
      if (!(err instanceof ApiError && err.status === 422 && applyFieldErrors(this.form, err))) {
        this.formError.set(errorMessage(err));
      }
    } finally {
      this.busy.set(false);
    }
  }

  private async loadCategories(): Promise<void> {
    try {
      this.categories.set(await firstValueFrom(this.api.listCategories()));
    } catch {
      // The category is optional: the form stays usable without the list.
      this.categories.set([]);
    }
  }
}
