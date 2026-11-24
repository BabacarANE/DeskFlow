import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { catchError, debounceTime, defer, distinctUntilChanged, map, of, switchMap } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ArticleSummary } from '../../core/api/models';

export const SUGGESTION_DEBOUNCE_MS = 400;
export const SUGGESTION_MIN_LENGTH = 3;
export const SUGGESTION_COUNT = 5;

/**
 * Article suggestions under the ticket title: published articles matching what is being typed.
 * Never blocks the form: a failed request just shows nothing.
 */
@Component({
  selector: 'app-article-suggestions',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <div aria-live="polite" class="mt-2">
      <p class="sr-only">{{ announcement() }}</p>
      @if (suggestions().length > 0) {
        <div class="rounded-md border border-line bg-primary-soft p-3">
          <p id="suggestions-title" class="text-sm font-medium">
            Ces articles pourraient vous aider
          </p>
          <ul class="mt-1 list-disc space-y-1 pl-5 text-sm" aria-labelledby="suggestions-title">
            @for (article of suggestions(); track article.id) {
              <li>
                <a [routerLink]="['/kb', article.id]" target="_blank" rel="noopener">
                  {{ article.title }}
                  <span class="sr-only">(s’ouvre dans un nouvel onglet)</span>
                </a>
              </li>
            }
          </ul>
        </div>
      }
    </div>
  `,
})
export class ArticleSuggestions {
  private readonly api = inject(ApiClient);

  /** The title being typed. */
  readonly term = input('');

  protected readonly suggestions = signal<ArticleSummary[]>([]);

  protected readonly announcement = computed(() => {
    const count = this.suggestions().length;
    if (count === 0) return '';
    return count === 1 ? '1 article suggéré.' : `${count} articles suggérés.`;
  });

  constructor() {
    toObservable(this.term)
      .pipe(
        debounceTime(SUGGESTION_DEBOUNCE_MS),
        map((text) => text.trim()),
        distinctUntilChanged(),
        // A new term cancels the request still in flight, so a late answer never overwrites it.
        switchMap((text) =>
          text.length < SUGGESTION_MIN_LENGTH
            ? of<ArticleSummary[]>([])
            : defer(() => this.api.listArticles({ q: text, pageSize: SUGGESTION_COUNT })).pipe(
                map((page) => page.items.slice(0, SUGGESTION_COUNT)),
                // Silent failure: suggestions are a help, not a requirement.
                catchError(() => of<ArticleSummary[]>([])),
              ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe((items) => this.suggestions.set(items));
  }
}
