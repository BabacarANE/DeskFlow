import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { CategoriesAdmin } from './categories-admin';
import { InvitationsList } from './invitations-list';
import { SlaSettings } from './sla-settings';
import { ThemeSettings } from './theme-settings';
import { UsersList } from './users-list';

export type AdminTab = 'users' | 'invitations' | 'categories' | 'sla' | 'theme';

export const ADMIN_TABS: { id: AdminTab; label: string }[] = [
  { id: 'users', label: 'Utilisateurs' },
  { id: 'invitations', label: 'Invitations' },
  { id: 'categories', label: 'Catégories' },
  { id: 'sla', label: 'SLA' },
  { id: 'theme', label: 'Thème' },
];

/** Tenant administration: one tab per area, keyboard operable (arrows, Home, End). */
@Component({
  selector: 'app-admin-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [UsersList, InvitationsList, CategoriesAdmin, SlaSettings, ThemeSettings],
  template: `
    <div class="space-y-6">
      <h1 class="text-2xl font-semibold">Administration de l’entreprise</h1>

      <div class="overflow-x-auto">
        <div
          role="tablist"
          aria-label="Rubriques d’administration"
          class="flex gap-1 border-b border-line"
        >
          @for (tab of tabs; track tab.id) {
            <button
              type="button"
              role="tab"
              class="min-h-11 whitespace-nowrap border-b-4 px-4 py-2 text-sm font-medium"
              [class.border-transparent]="active() !== tab.id"
              [class.border-ink]="active() === tab.id"
              [class.bg-primary-soft]="active() === tab.id"
              [id]="'admin-tab-' + tab.id"
              [attr.aria-selected]="active() === tab.id"
              [attr.aria-controls]="'admin-panel-' + tab.id"
              [attr.tabindex]="active() === tab.id ? 0 : -1"
              (click)="select(tab.id)"
              (keydown)="onKey($event, tab.id)"
            >
              {{ tab.label }}
            </button>
          }
        </div>
      </div>

      <!--
        A panel is created on the first visit, then only hidden: an unsaved edit (theme, SLA) is
        kept when the user moves to another tab and comes back.
      -->
      @for (tab of tabs; track tab.id) {
        @if (visited().has(tab.id)) {
          <div
            role="tabpanel"
            [id]="'admin-panel-' + tab.id"
            [attr.aria-labelledby]="'admin-tab-' + tab.id"
            [hidden]="active() !== tab.id"
          >
            @switch (tab.id) {
              @case ('users') {
                <app-users-list />
              }
              @case ('invitations') {
                <app-invitations-list />
              }
              @case ('categories') {
                <app-categories-admin />
              }
              @case ('sla') {
                <app-sla-settings />
              }
              @case ('theme') {
                <app-theme-settings />
              }
            }
          </div>
        }
      }
    </div>
  `,
})
export class AdminPage {
  protected readonly tabs = ADMIN_TABS;
  protected readonly active = signal<AdminTab>('users');
  protected readonly visited = signal<ReadonlySet<AdminTab>>(new Set<AdminTab>(['users']));

  protected select(tab: AdminTab): void {
    this.active.set(tab);
    this.visited.update((set) => (set.has(tab) ? set : new Set([...set, tab])));
  }

  protected onKey(event: KeyboardEvent, current: AdminTab): void {
    const index = ADMIN_TABS.findIndex((t) => t.id === current);
    const last = ADMIN_TABS.length - 1;
    let next: number;
    switch (event.key) {
      case 'ArrowRight':
        next = index === last ? 0 : index + 1;
        break;
      case 'ArrowLeft':
        next = index === 0 ? last : index - 1;
        break;
      case 'Home':
        next = 0;
        break;
      case 'End':
        next = last;
        break;
      default:
        return;
    }
    event.preventDefault();
    this.select(ADMIN_TABS[next].id);
    // Focus follows the selection (a tab with tabindex -1 can still be focused by script).
    const tablist = (event.currentTarget as HTMLElement).parentElement;
    tablist?.querySelectorAll<HTMLElement>('[role="tab"]')[next]?.focus();
  }
}
