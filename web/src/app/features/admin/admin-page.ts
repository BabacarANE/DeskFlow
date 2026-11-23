import { ChangeDetectionStrategy, Component } from '@angular/core';
import { InvitationsList } from './invitations-list';
import { UsersList } from './users-list';

/** Tenant administration: users and invitations (categories, SLA and theme come in phase 4). */
@Component({
  selector: 'app-admin-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [UsersList, InvitationsList],
  template: `
    <div class="space-y-10">
      <h1 class="text-2xl font-semibold">Administration de l’entreprise</h1>
      <app-users-list />
      <app-invitations-list />
    </div>
  `,
})
export class AdminPage {}
