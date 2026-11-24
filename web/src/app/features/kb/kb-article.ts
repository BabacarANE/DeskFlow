import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError, errorMessage } from '../../core/api/api-error';
import { ARTICLE_STATUS_LABELS } from '../../core/api/labels';
import { Article } from '../../core/api/models';
import { isAgentRole } from '../../core/api/ticket-rules';
import { AuthService } from '../../core/auth/auth.service';
import { NotFound } from '../errors/not-found';
import { StatusMessage } from '../../shared/status-message';

const PUBLISH_REFUSED =
  'Pour publier, l’article doit avoir un titre, un symptôme et une solution. Modifiez-le puis réessayez.';

/** Reads one article (Symptôme, Cause, Solution); agents can edit, publish and unpublish it. */
@Component({
  selector: 'app-kb-article',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, RouterLink, StatusMessage, NotFound],
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
        @if (article(); as a) {
          <article class="space-y-6" aria-labelledby="article-title">
            <div>
              <a routerLink="/kb" class="text-sm">← Retour à la base de connaissances</a>
              <div class="mt-2 flex flex-wrap items-center gap-3">
                <h1 id="article-title" class="text-2xl font-semibold">{{ a.title }}</h1>
                <span class="badge" [class.badge-muted]="a.status === 'Draft'">
                  {{ statusLabels[a.status] }}
                </span>
              </div>
              <p class="mt-1 text-sm text-ink-muted">
                Par {{ a.author.displayName }}
                @if (a.publishedAt) {
                  · publié le {{ a.publishedAt | date: 'dd/MM/yyyy' }}
                } @else {
                  · créé le {{ a.createdAt | date: 'dd/MM/yyyy' }}
                }
              </p>
              @if (a.tags.length > 0) {
                <ul class="mt-2 flex flex-wrap gap-1" aria-label="Étiquettes">
                  @for (tag of a.tags; track tag) {
                    <li class="badge badge-muted">{{ tag }}</li>
                  }
                </ul>
              }
              @if (canEdit() && a.sourceTicketId) {
                <p class="mt-2 text-sm">
                  Brouillon créé à partir du
                  <a [routerLink]="['/tickets', a.sourceTicketId]">ticket résolu</a>.
                </p>
              }
            </div>

            @if (canEdit()) {
              <div class="flex flex-wrap items-center gap-3">
                @if (a.status === 'Draft') {
                  <button
                    type="button"
                    class="btn btn-accent"
                    [disabled]="busy()"
                    (click)="publish()"
                  >
                    Publier
                  </button>
                  <a [routerLink]="['/kb', a.id, 'edit']" class="btn btn-secondary">Modifier</a>
                } @else {
                  <a [routerLink]="['/kb', a.id, 'edit']" class="btn btn-secondary">Modifier</a>
                  @if (confirmUnpublish()) {
                    <span class="text-sm">
                      Les demandeurs ne verront plus cet article. Dépublier ?
                    </span>
                    <button
                      type="button"
                      class="btn btn-secondary"
                      [disabled]="busy()"
                      (click)="unpublish()"
                    >
                      Confirmer la dépublication
                    </button>
                    <button
                      type="button"
                      class="btn btn-secondary"
                      (click)="confirmUnpublish.set(false)"
                    >
                      Annuler
                    </button>
                  } @else {
                    <button
                      type="button"
                      class="btn btn-secondary"
                      [disabled]="busy()"
                      (click)="confirmUnpublish.set(true)"
                    >
                      Dépublier
                    </button>
                  }
                }
              </div>
            }

            @if (actionError(); as message) {
              <p
                class="rounded-md border border-line bg-primary-soft p-3 text-sm font-medium"
                role="alert"
              >
                {{ message }}
              </p>
            }
            @if (notice(); as message) {
              <p
                class="rounded-md border border-line bg-primary-soft p-3 text-sm font-medium"
                role="status"
              >
                {{ message }}
              </p>
            }

            <div class="space-y-5">
              <section class="card space-y-1" aria-labelledby="section-symptom">
                <h2 id="section-symptom" class="text-lg font-semibold">Symptôme</h2>
                <p class="whitespace-pre-line">{{ a.symptom || 'Non renseigné.' }}</p>
              </section>
              <section class="card space-y-1" aria-labelledby="section-cause">
                <h2 id="section-cause" class="text-lg font-semibold">Cause</h2>
                <p class="whitespace-pre-line">{{ a.cause || 'Non renseignée.' }}</p>
              </section>
              <section class="card space-y-1" aria-labelledby="section-solution">
                <h2 id="section-solution" class="text-lg font-semibold">Solution</h2>
                <p class="whitespace-pre-line">{{ a.solution || 'Non renseignée.' }}</p>
              </section>
            </div>
          </article>
        }
      }
    }
  `,
})
export class KbArticle implements OnInit {
  private readonly api = inject(ApiClient);
  private readonly auth = inject(AuthService);
  private readonly id = inject(ActivatedRoute).snapshot.paramMap.get('id') ?? '';

  protected readonly statusLabels = ARTICLE_STATUS_LABELS;
  protected readonly canEdit = computed(() => isAgentRole(this.auth.role()));

  protected readonly state = signal<'loading' | 'ready' | 'notFound' | 'error'>('loading');
  protected readonly loadError = signal('');
  protected readonly article = signal<Article | null>(null);
  protected readonly busy = signal(false);
  protected readonly actionError = signal<string | null>(null);
  protected readonly notice = signal<string | null>(null);
  protected readonly confirmUnpublish = signal(false);

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    this.state.set('loading');
    try {
      this.article.set(await firstValueFrom(this.api.getArticle(this.id)));
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

  protected async publish(): Promise<void> {
    await this.change(() => this.api.publishArticle(this.id), 'Article publié.');
  }

  protected async unpublish(): Promise<void> {
    await this.change(() => this.api.unpublishArticle(this.id), 'Article repassé en brouillon.');
  }

  private async change(
    call: () => ReturnType<ApiClient['publishArticle']>,
    done: string,
  ): Promise<void> {
    this.actionError.set(null);
    this.notice.set(null);
    this.busy.set(true);
    try {
      this.article.set(await firstValueFrom(call()));
      this.notice.set(done);
    } catch (err) {
      this.actionError.set(
        err instanceof ApiError && err.status === 422 ? PUBLISH_REFUSED : errorMessage(err),
      );
    } finally {
      this.confirmUnpublish.set(false);
      this.busy.set(false);
    }
  }
}
