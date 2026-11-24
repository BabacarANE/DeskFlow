import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { ROLE_LABELS } from './core/api/labels';
import { Role } from './core/api/models';
import { isAgentRole } from './core/api/ticket-rules';
import { AuthService } from './core/auth/auth.service';
import { RealtimeService } from './core/realtime/realtime.service';
import { NotificationBell } from './shared/notification-bell';

interface NavItem {
  label: string;
  path: string;
  roles: readonly Role[];
}

const NAV: NavItem[] = [
  { label: 'Tickets', path: '/tickets', roles: ['TenantAdmin', 'Agent', 'Requester'] },
  {
    label: 'Base de connaissances',
    path: '/kb',
    roles: ['TenantAdmin', 'Agent', 'Requester'],
  },
  { label: 'Administration', path: '/admin', roles: ['TenantAdmin'] },
  { label: 'Entreprises', path: '/platform/tenants', roles: ['SuperAdmin'] },
];

@Component({
  imports: [RouterOutlet, RouterLink, RouterLinkActive, NotificationBell],
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './app.css',
  templateUrl: './app.html',
})
export class App {
  protected readonly auth = inject(AuthService);
  protected readonly roleLabels = ROLE_LABELS;
  protected readonly hasRealtime = computed(() => isAgentRole(this.auth.role()));

  constructor() {
    inject(RealtimeService).init();
  }

  protected readonly navItems = computed(() => {
    const role = this.auth.role();
    return role ? NAV.filter((item) => item.roles.includes(role)) : [];
  });

  protected logout(): void {
    void this.auth.logout();
  }
}
