import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { AuthService } from '../../core/auth/auth.service';

/** Provisional landing page: the ticket list arrives in phase 2. */
@Component({
  selector: 'app-tickets-home',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="space-y-3" aria-labelledby="tickets-title">
      <h1 id="tickets-title" class="text-2xl font-semibold">
        Bonjour {{ auth.user()?.displayName }}
      </h1>
      <p class="text-ink-muted">
        La liste des tickets arrive bientôt. Votre connexion fonctionne : vous êtes connecté·e à
        {{ auth.user()?.tenant?.name ?? 'la plateforme' }}.
      </p>
    </section>
  `,
})
export class TicketsHome {
  protected readonly auth = inject(AuthService);
}
