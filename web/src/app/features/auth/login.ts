import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NonNullableFormBuilder, ReactiveFormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { ApiClient } from '../../core/api/api-client';
import { errorMessage } from '../../core/api/api-error';
import { DEMO_ROLES } from '../../core/api/labels';
import { DemoRole, Provider } from '../../core/api/models';
import { AuthService, homeFor } from '../../core/auth/auth.service';

@Component({
  selector: 'app-login',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule],
  template: `
    <section class="mx-auto max-w-md space-y-6" aria-labelledby="login-title">
      <div>
        <h1 id="login-title" class="text-2xl font-semibold">Connexion</h1>
        <p class="mt-1 text-ink-muted">Plateforme de ticketing helpdesk multi-entreprises.</p>
      </div>

      @if (invitation()) {
        <p class="rounded-md border border-line bg-primary-soft p-3 text-sm" role="status">
          Vous avez une invitation. Connectez-vous avec le compte dont l’adresse e-mail correspond à
          celle de l’invitation.
        </p>
      }

      <div class="card space-y-3">
        <h2 class="font-semibold">Se connecter avec un compte existant</h2>
        @for (p of providers; track p.id) {
          <a class="btn btn-secondary w-full" [href]="url(p.id)">Continuer avec {{ p.label }}</a>
        }
        <p class="text-sm text-ink-muted">
          Le premier accès nécessite une invitation envoyée par l’administrateur de votre
          entreprise.
        </p>
      </div>

      <form class="card space-y-4" [formGroup]="demoForm" (ngSubmit)="startDemo()">
        <fieldset class="space-y-2">
          <legend class="font-semibold">Essayer la démo</legend>
          <p class="text-sm text-ink-muted">
            Sans compte, avec des données fictives. Choisissez un profil.
          </p>
          @for (r of demoRoles; track r.value) {
            <label class="flex min-h-11 cursor-pointer items-start gap-3 rounded-md px-2 py-1">
              <input type="radio" class="mt-1" formControlName="role" [value]="r.value" />
              <span>
                <span class="block font-medium">{{ r.label }}</span>
                <span class="block text-sm text-ink-muted">{{ r.hint }}</span>
              </span>
            </label>
          }
        </fieldset>

        @if (error(); as message) {
          <p
            class="rounded-md border border-line bg-primary-soft p-3 text-sm font-medium"
            role="alert"
          >
            {{ message }}
          </p>
        }

        <button type="submit" class="btn btn-accent w-full" [disabled]="busy()">
          {{ busy() ? 'Connexion…' : 'Essayer la démo' }}
        </button>
      </form>
    </section>
  `,
})
export class Login {
  private readonly api = inject(ApiClient);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly query = toSignal(inject(ActivatedRoute).queryParamMap);

  protected readonly providers: { id: Provider; label: string }[] = [
    { id: 'google', label: 'Google' },
    { id: 'github', label: 'GitHub' },
  ];
  protected readonly demoRoles = DEMO_ROLES;
  /** The invitation token from `?invitation=` is carried into the OAuth redirect. */
  protected readonly invitation = computed(() => this.query()?.get('invitation') ?? null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly demoForm = inject(NonNullableFormBuilder).group({
    role: 'demandeur' as DemoRole,
  });

  protected url(provider: Provider): string {
    return this.api.externalLoginUrl(provider, this.invitation());
  }

  protected async startDemo(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      const me = await this.auth.loginDemo(this.demoForm.controls.role.value);
      await this.router.navigateByUrl(homeFor(me.role));
    } catch (err) {
      this.error.set(errorMessage(err));
    } finally {
      this.busy.set(false);
    }
  }
}
