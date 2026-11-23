import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'app-not-found',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <section class="mx-auto max-w-md space-y-4 text-center">
      <p class="text-5xl font-semibold" aria-hidden="true">404</p>
      <h2 class="text-2xl font-semibold">Page introuvable</h2>
      <p class="text-ink-muted">Cette adresse n’existe pas ou n’existe plus.</p>
      <a routerLink="/" class="btn btn-secondary">Retour à l’accueil</a>
    </section>
  `,
})
export class NotFound {}
