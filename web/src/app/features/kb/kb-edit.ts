import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import {
  AbstractControl,
  NonNullableFormBuilder,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError, errorMessage } from '../../core/api/api-error';
import { Article, ArticleRequest } from '../../core/api/models';
import { FieldError, applyFieldErrors, describedBy } from '../../shared/field-error';
import { StatusMessage } from '../../shared/status-message';
import { NotFound } from '../errors/not-found';
import {
  CAUSE_MAX,
  SOLUTION_MAX,
  SYMPTOM_MAX,
  TAG_MAX_COUNT,
  TAG_MAX_LENGTH,
  TITLE_MAX,
  TITLE_MIN,
  missingForPublish,
  parseTags,
  tagsError,
} from './kb-rules';

function tagsValidator(control: AbstractControl): ValidationErrors | null {
  const message = tagsError(String(control.value));
  return message ? { custom: { message } } : null;
}

/** Create (`/kb/new`) or edit (`/kb/:id/edit`) an article, then optionally publish it. */
@Component({
  selector: 'app-kb-edit',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, RouterLink, FieldError, StatusMessage, NotFound],
  template: `
    @switch (state()) {
      @case ('loading') {
        <app-status-message kind="loading" message="Chargement de l’article…" />
      }
      @case ('notFound') {
        <app-not-found />
      }
      @case ('error') {
        <app-status-message kind="error" [message]="loadError()" (retry)="load()" />
      }
      @default {
        <section class="space-y-6" aria-labelledby="edit-title">
          <div>
            <a [routerLink]="cancelLink()" class="text-sm">← Retour</a>
            <h1 id="edit-title" class="mt-2 text-2xl font-semibold">
              {{ articleId() ? 'Modifier l’article' : 'Nouvel article' }}
            </h1>
            @if (status() === 'Published') {
              <p class="text-sm text-ink-muted">
                Cet article est publié : vos modifications sont visibles immédiatement.
              </p>
            }
          </div>

          <form class="card space-y-5" [formGroup]="form" (ngSubmit)="submit(false)" novalidate>
            <div>
              <label for="article-title-input" class="mb-1 block text-sm font-medium">Titre</label>
              <input
                id="article-title-input"
                type="text"
                class="field"
                autocomplete="off"
                [maxLength]="titleMax"
                formControlName="title"
                [attr.aria-describedby]="describe(form.controls.title, 'article-title-error')"
              />
              <app-field-error [control]="form.controls.title" errorId="article-title-error" />
            </div>

            <div>
              <label for="article-symptom" class="mb-1 block text-sm font-medium">Symptôme</label>
              <textarea
                id="article-symptom"
                class="field"
                rows="5"
                [maxLength]="symptomMax"
                formControlName="symptom"
                [attr.aria-describedby]="
                  describe(form.controls.symptom, 'article-symptom-error', 'article-symptom-hint')
                "
              ></textarea>
              <p id="article-symptom-hint" class="mt-1 text-sm text-ink-muted">
                Ce que l’utilisateur observe : message d’erreur, comportement. Obligatoire pour
                publier.
              </p>
              <app-field-error [control]="form.controls.symptom" errorId="article-symptom-error" />
            </div>

            <div>
              <label for="article-cause" class="mb-1 block text-sm font-medium">Cause</label>
              <textarea
                id="article-cause"
                class="field"
                rows="4"
                [maxLength]="causeMax"
                formControlName="cause"
                [attr.aria-describedby]="
                  describe(form.controls.cause, 'article-cause-error', 'article-cause-hint')
                "
              ></textarea>
              <p id="article-cause-hint" class="mt-1 text-sm text-ink-muted">
                Pourquoi cela arrive. Facultative.
              </p>
              <app-field-error [control]="form.controls.cause" errorId="article-cause-error" />
            </div>

            <div>
              <label for="article-solution" class="mb-1 block text-sm font-medium">Solution</label>
              <textarea
                id="article-solution"
                class="field"
                rows="7"
                [maxLength]="solutionMax"
                formControlName="solution"
                [attr.aria-describedby]="
                  describe(
                    form.controls.solution,
                    'article-solution-error',
                    'article-solution-hint'
                  )
                "
              ></textarea>
              <p id="article-solution-hint" class="mt-1 text-sm text-ink-muted">
                Les étapes pour résoudre le problème. Obligatoire pour publier.
              </p>
              <app-field-error
                [control]="form.controls.solution"
                errorId="article-solution-error"
              />
            </div>

            <div>
              <label for="article-tags" class="mb-1 block text-sm font-medium">Étiquettes</label>
              <input
                id="article-tags"
                type="text"
                class="field"
                autocomplete="off"
                formControlName="tags"
                [attr.aria-describedby]="
                  describe(form.controls.tags, 'article-tags-error', 'article-tags-hint')
                "
              />
              <p id="article-tags-hint" class="mt-1 text-sm text-ink-muted">
                Séparées par des virgules : {{ tagMaxCount }} au plus, {{ tagMaxLength }} caractères
                chacune. {{ tagCount() }} sur {{ tagMaxCount }}.
              </p>
              <app-field-error [control]="form.controls.tags" errorId="article-tags-error" />
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
                {{ busy() ? 'Enregistrement…' : 'Enregistrer' }}
              </button>
              @if (status() !== 'Published') {
                <button
                  type="button"
                  class="btn btn-secondary"
                  [disabled]="busy()"
                  (click)="submit(true)"
                >
                  Enregistrer et publier
                </button>
              }
              <a [routerLink]="cancelLink()" class="btn btn-secondary">Annuler</a>
            </div>
          </form>
        </section>
      }
    }
  `,
})
export class KbEdit implements OnInit {
  private readonly api = inject(ApiClient);
  private readonly router = inject(Router);
  private readonly routeId = inject(ActivatedRoute).snapshot.paramMap.get('id');

  protected readonly titleMax = TITLE_MAX;
  protected readonly symptomMax = SYMPTOM_MAX;
  protected readonly causeMax = CAUSE_MAX;
  protected readonly solutionMax = SOLUTION_MAX;
  protected readonly tagMaxCount = TAG_MAX_COUNT;
  protected readonly tagMaxLength = TAG_MAX_LENGTH;

  protected readonly form = inject(NonNullableFormBuilder).group({
    title: [
      '',
      [Validators.required, Validators.minLength(TITLE_MIN), Validators.maxLength(TITLE_MAX)],
    ],
    symptom: ['', Validators.maxLength(SYMPTOM_MAX)],
    cause: ['', Validators.maxLength(CAUSE_MAX)],
    solution: ['', Validators.maxLength(SOLUTION_MAX)],
    tags: ['', tagsValidator],
  });

  protected readonly state = signal<'loading' | 'ready' | 'notFound' | 'error'>('loading');
  protected readonly loadError = signal('');
  protected readonly busy = signal(false);
  protected readonly formError = signal<string | null>(null);
  /** Known once the article exists: edit mode from the start, create mode after a first save. */
  protected readonly articleId = signal<string | null>(this.routeId);
  protected readonly status = signal<Article['status'] | null>(null);
  private readonly tagsText = signal('');

  protected readonly tagCount = computed(() => parseTags(this.tagsText()).length);
  protected readonly cancelLink = computed(() => {
    const id = this.articleId();
    return id ? ['/kb', id] : ['/kb'];
  });

  constructor() {
    this.form.controls.tags.valueChanges.subscribe((text) => this.tagsText.set(text));
  }

  ngOnInit(): void {
    void this.load();
  }

  protected describe(control: AbstractControl, errorId: string, ...hints: string[]): string | null {
    return describedBy(control, errorId, ...hints);
  }

  async load(): Promise<void> {
    if (!this.routeId) {
      this.state.set('ready');
      return;
    }
    this.state.set('loading');
    try {
      const article = await firstValueFrom(this.api.getArticle(this.routeId));
      this.form.reset({
        title: article.title,
        symptom: article.symptom,
        cause: article.cause,
        solution: article.solution,
        tags: article.tags.join(', '),
      });
      this.status.set(article.status);
      this.state.set('ready');
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) {
        this.state.set('notFound');
      } else {
        this.loadError.set(errorMessage(err));
        this.state.set('error');
      }
    }
  }

  /** Saves, and publishes afterwards when asked. A refused publication keeps the saved draft. */
  protected async submit(publish: boolean): Promise<void> {
    this.formError.set(null);
    // Recomputes the validators: drops errors left by an earlier publish check or a 422.
    Object.values(this.form.controls).forEach((control) => control.updateValueAndValidity());
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const value = this.form.getRawValue();
    if (publish && !this.checkPublishable(value)) return;

    const body: ArticleRequest = {
      title: value.title.trim(),
      symptom: value.symptom.trim(),
      cause: value.cause.trim(),
      solution: value.solution.trim(),
      tags: parseTags(value.tags),
    };
    this.busy.set(true);
    try {
      const id = this.articleId();
      const saved = id
        ? await firstValueFrom(this.api.updateArticle(id, body))
        : await firstValueFrom(this.api.createArticle(body));
      this.articleId.set(saved.id);
      this.status.set(saved.status);
      if (publish) {
        try {
          await firstValueFrom(this.api.publishArticle(saved.id));
          this.status.set('Published');
        } catch (err) {
          this.formError.set(
            `L’article est enregistré en brouillon, mais n’a pas été publié : ${this.publishMessage(err)}`,
          );
          return;
        }
      }
      await this.router.navigate(['/kb', saved.id]);
    } catch (err) {
      if (!(err instanceof ApiError && err.status === 422 && applyFieldErrors(this.form, err))) {
        this.formError.set(errorMessage(err));
      }
    } finally {
      this.busy.set(false);
    }
  }

  /** Publishing needs a title, a symptom and a solution: checked before any request. */
  private checkPublishable(value: { title: string; symptom: string; solution: string }): boolean {
    const missing = missingForPublish(value);
    const required = {
      symptom: 'Le symptôme est obligatoire pour publier.',
      solution: 'La solution est obligatoire pour publier.',
    } as const;
    let ok = true;
    for (const field of ['symptom', 'solution'] as const) {
      if (!missing[field]) continue;
      ok = false;
      this.form.controls[field].setErrors({ custom: { message: required[field] } });
      this.form.controls[field].markAsTouched();
    }
    return ok;
  }

  private publishMessage(err: unknown): string {
    if (err instanceof ApiError && err.status === 422) {
      return 'le titre, le symptôme et la solution sont obligatoires.';
    }
    return errorMessage(err);
  }
}
