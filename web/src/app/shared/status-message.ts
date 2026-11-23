import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

/** Loading, empty or error block shared by every list (SPEC section 10). */
@Component({
  selector: 'app-status-message',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @switch (kind()) {
      @case ('loading') {
        <p class="py-6 text-center text-ink-muted" role="status">
          {{ message() || 'Chargement…' }}
        </p>
      }
      @case ('empty') {
        <p class="py-6 text-center text-ink-muted">{{ message() }}</p>
      }
      @case ('error') {
        <div class="rounded-md border border-line bg-primary-soft p-4" role="alert">
          <p class="font-medium">{{ message() }}</p>
          <button type="button" class="btn btn-secondary btn-small mt-3" (click)="retry.emit()">
            Réessayer
          </button>
        </div>
      }
    }
  `,
})
export class StatusMessage {
  readonly kind = input.required<'loading' | 'empty' | 'error'>();
  readonly message = input('');
  readonly retry = output<void>();
}
