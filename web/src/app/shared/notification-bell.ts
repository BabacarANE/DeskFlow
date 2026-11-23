import { CdkConnectedOverlay, CdkOverlayOrigin, ConnectedPosition } from '@angular/cdk/overlay';
import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { Router } from '@angular/router';
import { AppNotification, NotificationsState } from '../core/realtime/notifications.state';
import { RealtimeService } from '../core/realtime/realtime.service';

@Component({
  selector: 'app-notification-bell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CdkConnectedOverlay, CdkOverlayOrigin, DatePipe],
  template: `
    <button
      #trigger
      type="button"
      cdkOverlayOrigin
      #origin="cdkOverlayOrigin"
      class="btn btn-secondary btn-small"
      aria-haspopup="dialog"
      [attr.aria-expanded]="isOpen()"
      [attr.aria-label]="label()"
      (click)="toggle()"
    >
      <svg
        aria-hidden="true"
        focusable="false"
        viewBox="0 0 24 24"
        class="size-5"
        fill="none"
        stroke="currentColor"
        stroke-width="2"
        stroke-linecap="round"
        stroke-linejoin="round"
      >
        <path d="M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9" />
        <path d="M10.3 21a1.94 1.94 0 0 0 3.4 0" />
      </svg>
      @if (state.unreadCount() > 0) {
        <span class="badge" aria-hidden="true">{{ countText() }}</span>
      }
    </button>

    <div class="sr-only" aria-live="polite" data-testid="announcer">
      {{ state.announcement() }}
    </div>

    <ng-template
      cdkConnectedOverlay
      [cdkConnectedOverlayOrigin]="origin"
      [cdkConnectedOverlayOpen]="isOpen()"
      [cdkConnectedOverlayPositions]="positions"
      (overlayKeydown)="onKeydown($event)"
      (overlayOutsideClick)="onOutsideClick($event)"
      (attach)="focusFirst()"
      (detach)="isOpen.set(false)"
    >
      <div
        #panel
        role="dialog"
        aria-label="Notifications"
        tabindex="-1"
        class="w-80 max-w-[calc(100vw-2rem)] space-y-3 rounded-lg border border-line bg-surface p-4 text-ink shadow-lg"
      >
        <div class="flex items-center justify-between gap-2">
          <h2 class="text-base font-semibold">Notifications</h2>
          @if (state.unreadCount() > 0) {
            <button type="button" class="btn btn-secondary btn-small" (click)="markAll()">
              Tout marquer comme lu
            </button>
          }
        </div>

        @if (realtime.status() === 'offline') {
          <p class="text-sm text-ink-muted">
            Temps réel indisponible : reconnexion en cours. Les tickets restent à jour au
            rechargement.
          </p>
        }

        @if (state.items().length === 0) {
          <p class="text-sm text-ink-muted">Aucune notification pour le moment.</p>
        } @else {
          <ul class="max-h-96 divide-y divide-line overflow-y-auto">
            @for (item of state.items(); track item.id) {
              <li>
                <button
                  type="button"
                  class="block min-h-11 w-full px-1 py-2 text-left text-sm hover:bg-primary-soft"
                  [class.font-semibold]="!item.read"
                  (click)="open(item)"
                >
                  <span class="block">{{ item.text }}</span>
                  <span class="block text-xs font-normal text-ink-muted">
                    {{ item.at | date: 'dd/MM HH:mm' }}{{ item.read ? '' : ' · Non lue' }}
                  </span>
                </button>
              </li>
            }
          </ul>
        }
      </div>
    </ng-template>
  `,
})
export class NotificationBell {
  protected readonly state = inject(NotificationsState);
  protected readonly realtime = inject(RealtimeService);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);

  private readonly trigger = viewChild.required<ElementRef<HTMLButtonElement>>('trigger');
  private readonly panel = viewChild<ElementRef<HTMLElement>>('panel');

  protected readonly isOpen = signal(false);
  protected readonly positions: ConnectedPosition[] = [
    { originX: 'end', originY: 'bottom', overlayX: 'end', overlayY: 'top', offsetY: 8 },
    { originX: 'end', originY: 'top', overlayX: 'end', overlayY: 'bottom', offsetY: -8 },
  ];

  protected readonly countText = computed(() => {
    const n = this.state.unreadCount();
    return n > 99 ? '99+' : String(n);
  });
  protected readonly label = computed(() => {
    const n = this.state.unreadCount();
    const unread = n === 0 ? 'aucune non lue' : n === 1 ? '1 non lue' : `${n} non lues`;
    return `Notifications, ${unread}${this.realtime.status() === 'offline' ? ', hors ligne' : ''}`;
  });

  protected toggle(): void {
    if (this.isOpen()) this.close(true);
    else this.isOpen.set(true);
  }

  protected focusFirst(): void {
    afterNextRender(
      () => {
        const first = this.buttons()[0];
        (first ?? this.panel()?.nativeElement)?.focus();
      },
      { injector: this.injector },
    );
  }

  protected onOutsideClick(event: MouseEvent): void {
    if (this.trigger().nativeElement.contains(event.target as Node)) return; // the toggle handles it
    this.close(false);
  }

  protected onKeydown(event: KeyboardEvent): void {
    const buttons = this.buttons();
    const index = buttons.indexOf(document.activeElement as HTMLButtonElement);
    switch (event.key) {
      case 'Escape':
        event.preventDefault();
        this.close(true);
        break;
      case 'ArrowDown':
        event.preventDefault();
        buttons[Math.min(index + 1, buttons.length - 1)]?.focus();
        break;
      case 'ArrowUp':
        event.preventDefault();
        buttons[Math.max(index - 1, 0)]?.focus();
        break;
      case 'Home':
        event.preventDefault();
        buttons[0]?.focus();
        break;
      case 'End':
        event.preventDefault();
        buttons[buttons.length - 1]?.focus();
        break;
      case 'Tab':
        // The panel lives at the end of the document: leaving it closes it and gives focus back.
        if (buttons.length === 0 || (event.shiftKey ? index <= 0 : index === buttons.length - 1)) {
          event.preventDefault();
          this.close(true);
        }
        break;
    }
  }

  protected markAll(): void {
    this.state.markAllRead();
    // The button disappears once nothing is unread: keep focus inside the panel.
    this.panel()?.nativeElement.focus();
  }

  protected open(item: AppNotification): void {
    this.state.markRead(item.id);
    this.close(true);
    void this.router.navigate(['/tickets', item.ticketId]);
  }

  private close(restoreFocus: boolean): void {
    this.isOpen.set(false);
    if (restoreFocus) this.trigger().nativeElement.focus();
  }

  private buttons(): HTMLButtonElement[] {
    const panel = this.panel()?.nativeElement;
    return panel ? Array.from(panel.querySelectorAll('button')) : [];
  }
}
