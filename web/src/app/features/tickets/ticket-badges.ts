import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { PRIORITY_LABELS, STATUS_LABELS } from '../../core/api/labels';
import { TicketPriority, TicketStatus } from '../../core/api/models';

/**
 * Status badge: the text always says the status; colour only reinforces it.
 * Colours come from the palette tokens through the `.badge-*` classes of styles.css.
 */
@Component({
  selector: 'app-status-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="badge" [class]="css()">{{ label() }}</span>`,
})
export class StatusBadge {
  readonly status = input.required<TicketStatus>();

  protected readonly label = computed(() => STATUS_LABELS[this.status()]);
  protected readonly css = computed(
    () =>
      ({
        New: 'badge-primary',
        Open: '',
        Pending: 'badge-muted',
        Resolved: 'badge-outline',
        Closed: 'badge-muted',
      })[this.status()],
  );
}

/** Priority badge. Urgent takes the accent (SPEC section 10); High is outlined. */
@Component({
  selector: 'app-priority-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="badge" [class]="css()">{{ label() }}</span>`,
})
export class PriorityBadge {
  readonly priority = input.required<TicketPriority>();

  protected readonly label = computed(() => PRIORITY_LABELS[this.priority()]);
  protected readonly css = computed(
    () =>
      ({
        Low: 'badge-muted',
        Normal: '',
        High: 'badge-outline',
        Urgent: 'badge-accent',
      })[this.priority()],
  );
}

/** SLA alert: accent background with the contrast-safe `--on-accent` text. */
@Component({
  selector: 'app-sla-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="badge badge-accent">Échéance SLA dépassée</span>`,
})
export class SlaBadge {}
