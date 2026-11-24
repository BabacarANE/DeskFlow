import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import {
  Subject,
  catchError,
  debounceTime,
  distinctUntilChanged,
  map,
  of,
  switchMap,
  tap,
} from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { errorMessage } from '../../core/api/api-error';
import { ARTICLE_STATUS_LABELS } from '../../core/api/labels';
import { ArticleStatus, ArticleSummary } from '../../core/api/models';
import { isAgentRole } from '../../core/api/ticket-rules';
import { AuthService } from '../../core/auth/auth.service';
import { Pager } from '../../shared/pager';
import { StatusMessage } from '../../shared/status-message';

export const KB_SEARCH_DEBOUNCE_MS = 300;
const PAGE_SIZE = 10;

interface Params {
  q: string;
  status: ArticleStatus | '';
  page: number;
  /** Bumped by "Réessayer" so that the same parameters are requested again. */
  attempt: number;
}

/** Knowledge base list: search with debounce, pagination, status filter for agents only. */
@Component({
  selector: 'app-kb-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, RouterLink, Pager, StatusMessage],
  template: `
    <section class="space-y-6" aria-labelledby="kb-title">
      <div class="flex flex-wrap items-center justify-between gap-3">
        <h1 id="kb-title" class="text-2xl font-semibold">Base de connaissances</h1>
        @if (canEdit()) {
          <a routerLink="/kb/new" class="btn btn-accent">Nouvel article</a>
        }
      </div>

      <form
        class="card flex flex-wrap items-end gap-4"
        role="search"
        aria-label="Rechercher dans la base de connaissances"
        (ngSubmit)="searchNow()"
      >
        <div class="min-w-56 flex-1">
          <label for="kb-search" class="mb-1 block text-sm font-medium">Rechercher</label>
          <input
            id="kb-search"
            type="search"
            class="field"
            maxlength="100"
            autocomplete="off"
            placeholder="Mots du titre, du symptôme, de la solution…"
            name="q"
            [ngModel]="input()"
            (ngModelChange)="onInput($event)"
          />
        </div>
        @if (canEdit()) {
          <div>
            <label for="kb-status" class="mb-1 block text-sm font-medium">Statut</label>
            <select
              id="kb-status"
              class="field"
              name="status"
              [ngModel]="params().status"
              (ngModelChange)="setStatus($event)"
            >
              <option value="">Tous les statuts</option>
              <option value="Published">Publiés</option>
              <option value="Draft">Brouillons</option>
            </select>
          </div>
        }
        <button type="submit" class="btn btn-secondary">Rechercher</button>
      </form>

      <p class="sr-only" role="status" aria-live="polite">{{ announcement() }}</p>

      @switch (state()) {
        @case ('loading') {
          <app-status-message kind="loading" message="Chargement des articles…" />
        }
        @case ('error') {
          <app-status-message kind="error" [message]="loadError()" (retry)="retry()" />
        }
        @default {
          @if (articles().length === 0) {
            <app-status-message kind="empty" [message]="emptyMessage()" />
          } @else {
            <ul class="space-y-3">
              @for (article of articles(); track article.id) {
                <li class="card space-y-2">
                  <div class="flex flex-wrap items-center gap-2">
                    <h2 class="text-lg font-semibold">
                      <a [routerLink]="['/kb', article.id]">{{ article.title }}</a>
                    </h2>
                    <span class="badge" [class.badge-muted]="article.status === 'Draft'">
                      {{ statusLabels[article.status] }}
                    </span>
                  </div>
                  @if (article.excerpt) {
                    <p class="text-sm">{{ article.excerpt }}</p>
                  }
                  <div class="flex flex-wrap items-center gap-2 text-sm text-ink-muted">
                    @if (article.publishedAt) {
                      <span>Publié le {{ article.publishedAt | date: 'dd/MM/yyyy' }}</span>
                    } @else {
                      <span>Créé le {{ article.createdAt | date: 'dd/MM/yyyy' }}</span>
                    }
                    @if (article.tags.length > 0) {
                      <ul class="flex flex-wrap gap-1" aria-label="Étiquettes">
                        @for (tag of article.tags; track tag) {
                          <li class="badge badge-muted">{{ tag }}</li>
                        }
                      </ul>
                    }
                  </div>
                </li>
              }
            </ul>
            <app-pager
              [page]="params().page"
              [pageSize]="pageSize"
              [total]="total()"
              (pageChange)="goTo($event)"
            />
          }
        }
      }
    </section>
  `,
})
export class KbList {
  private readonly api = inject(ApiClient);
  private readonly auth = inject(AuthService);

  protected readonly pageSize = PAGE_SIZE;
  protected readonly statusLabels = ARTICLE_STATUS_LABELS;

  /** The status filter and the drafts only exist for agents; the API ignores it for a Requester. */
  protected readonly canEdit = computed(() => isAgentRole(this.auth.role()));

  protected readonly input = signal('');
  protected readonly params = signal<Params>({ q: '', status: '', page: 1, attempt: 0 });

  protected readonly state = signal<'loading' | 'ready' | 'error'>('loading');
  protected readonly loadError = signal('');
  protected readonly articles = signal<ArticleSummary[]>([]);
  protected readonly total = signal(0);

  private readonly typed = new Subject<string>();

  protected readonly announcement = computed(() => {
    if (this.state() !== 'ready') return '';
    const total = this.total();
    if (total === 0) return 'Aucun article trouvé.';
    return total === 1 ? '1 article trouvé.' : `${total} articles trouvés.`;
  });

  protected readonly emptyMessage = computed(() => {
    const { q, status } = this.params();
    if (q) return `Aucun article ne correspond à « ${q} ».`;
    if (status === 'Draft') return 'Aucun brouillon pour le moment.';
    return 'Aucun article pour le moment.';
  });

  constructor() {
    // Typing waits for a pause (300 ms); the same text twice does not search twice.
    this.typed
      .pipe(
        debounceTime(KB_SEARCH_DEBOUNCE_MS),
        map((text) => text.trim()),
        distinctUntilChanged(),
        takeUntilDestroyed(),
      )
      .subscribe((q) => this.params.update((p) => (p.q === q ? p : { ...p, q, page: 1 })));

    // switchMap drops the answer of an obsolete request.
    toObservable(this.params)
      .pipe(
        tap(() => this.state.set('loading')),
        switchMap((p) =>
          this.api
            .listArticles({
              q: p.q || undefined,
              status: this.canEdit() && p.status ? p.status : undefined,
              page: p.page,
              pageSize: PAGE_SIZE,
            })
            .pipe(
              map((page) => ({ page, error: null as string | null })),
              catchError((err: unknown) => of({ page: null, error: errorMessage(err) })),
            ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe(({ page, error }) => {
        if (page) {
          this.articles.set(page.items);
          this.total.set(page.total);
          this.state.set('ready');
        } else {
          this.loadError.set(error ?? '');
          this.state.set('error');
        }
      });
  }

  protected onInput(text: string): void {
    this.input.set(text);
    this.typed.next(text);
  }

  /** Enter or the button: search at once, without waiting for the pause. */
  protected searchNow(): void {
    const q = this.input().trim();
    this.params.update((p) => ({ ...p, q, page: 1 }));
  }

  protected setStatus(status: ArticleStatus | ''): void {
    this.params.update((p) => ({ ...p, status, page: 1 }));
  }

  protected goTo(page: number): void {
    this.params.update((p) => ({ ...p, page }));
  }

  protected retry(): void {
    this.params.update((p) => ({ ...p, attempt: p.attempt + 1 }));
  }
}
