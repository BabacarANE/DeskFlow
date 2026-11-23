import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';

@Component({
  selector: 'app-pager',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (pageCount() > 1) {
      <nav class="mt-4 flex items-center justify-between gap-2" aria-label="Pagination">
        <button
          type="button"
          class="btn btn-secondary btn-small"
          [disabled]="page() <= 1"
          (click)="pageChange.emit(page() - 1)"
        >
          Précédent
        </button>
        <span class="text-sm text-ink-muted" aria-live="polite">
          Page {{ page() }} sur {{ pageCount() }}
        </span>
        <button
          type="button"
          class="btn btn-secondary btn-small"
          [disabled]="page() >= pageCount()"
          (click)="pageChange.emit(page() + 1)"
        >
          Suivant
        </button>
      </nav>
    }
  `,
})
export class Pager {
  readonly page = input.required<number>();
  readonly pageSize = input.required<number>();
  readonly total = input.required<number>();
  readonly pageChange = output<number>();

  protected readonly pageCount = computed(() =>
    Math.max(1, Math.ceil(this.total() / this.pageSize())),
  );
}
