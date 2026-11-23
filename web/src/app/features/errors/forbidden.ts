import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'app-forbidden',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <section class="mx-auto max-w-md space-y-4 text-center">
      <p class="text-5xl font-semibold" aria-hidden="true">403</p>
      <h2 class="text-2xl font-semibold">Accès refusé</h2>
      <p class="text-ink-muted">Votre rôle ne permet pas d’ouvrir cette page.</p>
      <a routerLink="/" class="btn btn-secondary">Retour à l’accueil</a>
    </section>
  `,
})
export class Forbidden {}
