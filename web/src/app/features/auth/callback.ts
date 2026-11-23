import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AuthService, homeFor } from '../../core/auth/auth.service';
import { authErrorMessage } from './auth-errors';

/** Landing page of the OAuth redirect: shows the failure, or restores the session and moves on. */
@Component({
  selector: 'app-callback',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <section class="mx-auto max-w-md space-y-4" aria-labelledby="callback-title">
      <h1 id="callback-title" class="text-2xl font-semibold">Connexion</h1>
      @if (error(); as message) {
        <div class="card space-y-4" role="alert">
          <p class="font-medium">{{ message }}</p>
          <a routerLink="/login" class="btn btn-secondary">Retour à la connexion</a>
        </div>
      } @else {
        <p class="text-ink-muted" role="status">Connexion en cours…</p>
      }
    </section>
  `,
})
export class Callback implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);

  protected readonly error = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    const code = this.route.snapshot.queryParamMap.get('error');
    if (code) {
      this.error.set(authErrorMessage(code));
      return;
    }
    // Shared with the startup restore: the refresh endpoint is called once.
    const signedIn = await this.auth.restoreSession();
    const role = this.auth.role();
    if (signedIn && role) {
      await this.router.navigateByUrl(homeFor(role), { replaceUrl: true });
    } else {
      this.error.set('Votre session n’a pas pu être ouverte. Reconnectez-vous.');
    }
  }
}
