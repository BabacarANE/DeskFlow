import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError, errorMessage } from '../../core/api/api-error';
import { EVENT_LABELS, PRIORITIES, PRIORITY_LABELS, STATUS_LABELS } from '../../core/api/labels';
import {
  Category,
  PROBLEM_CONCURRENCY_CONFLICT,
  PROBLEM_INVALID_TRANSITION,
  TicketDetail as Ticket,
  TicketEvent,
  UpdateTicketRequest,
  UserRef,
} from '../../core/api/models';
import { allowedTransitions, isAgentRole, isSlaBreachedDetail } from '../../core/api/ticket-rules';
import { AuthService } from '../../core/auth/auth.service';
import { RealtimeService } from '../../core/realtime/realtime.service';
import { FieldError, applyFieldErrors } from '../../shared/field-error';
import { StatusMessage } from '../../shared/status-message';
import { PriorityBadge, SlaBadge, StatusBadge } from './ticket-badges';

type TicketChange = Omit<UpdateTicketRequest, 'rowVersion'>;

interface Conflict {
  /** What the agent tried to do, to offer re-applying it on the fresh version. */
  change: TicketChange;
  summary: string;
}

@Component({
  selector: 'app-ticket-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    DatePipe,
    ReactiveFormsModule,
    RouterLink,
    FieldError,
    StatusMessage,
    StatusBadge,
    PriorityBadge,
    SlaBadge,
  ],
  template: `
    <section class="space-y-6" aria-labelledby="ticket-title">
      <a routerLink="/tickets" class="text-sm">← Retour aux tickets</a>

      @switch (status()) {
        @case ('loading') {
          <app-status-message kind="loading" message="Chargement du ticket…" />
        }
        @case ('notfound') {
          <div class="card space-y-3">
            <h1 id="ticket-title" class="text-2xl font-semibold">Ticket introuvable</h1>
            <p class="text-ink-muted">
              Ce ticket n’existe pas, ou vous n’avez pas le droit de le consulter.
            </p>
          </div>
        }
        @case ('error') {
          <app-status-message kind="error" [message]="loadError()" (retry)="load()" />
        }
        @default {
          @if (ticket(); as t) {
            <header class="space-y-3">
              <h1 id="ticket-title" class="text-2xl font-semibold">
                <span class="text-ink-muted">#{{ t.number }}</span> {{ t.title }}
              </h1>
              <div class="flex flex-wrap items-center gap-2">
                <app-status-badge [status]="t.status" />
                <app-priority-badge [priority]="t.priority" />
                @if (isAgent() && breached()) {
                  <app-sla-badge />
                }
              </div>
              <dl class="grid gap-x-6 gap-y-1 text-sm sm:grid-cols-2">
                <div class="flex gap-2">
                  <dt class="text-ink-muted">Demandeur</dt>
                  <dd>{{ t.requester.displayName }}</dd>
                </div>
                <div class="flex gap-2">
                  <dt class="text-ink-muted">Assigné à</dt>
                  <dd>{{ t.assignee?.displayName ?? 'Non assigné' }}</dd>
                </div>
                <div class="flex gap-2">
                  <dt class="text-ink-muted">Catégorie</dt>
                  <dd>{{ t.category?.name ?? 'Aucune' }}</dd>
                </div>
                <div class="flex gap-2">
                  <dt class="text-ink-muted">Créé le</dt>
                  <dd>{{ t.createdAt | date: 'dd/MM/yyyy HH:mm' }}</dd>
                </div>
                @if (isAgent() && t.resolutionDueAt) {
                  <div class="flex gap-2">
                    <dt class="text-ink-muted">Échéance de résolution</dt>
                    <dd>{{ t.resolutionDueAt | date: 'dd/MM/yyyy HH:mm' }}</dd>
                  </div>
                }
              </dl>
            </header>

            <p class="sr-only" role="status">{{ notice() }}</p>

            @if (conflict(); as c) {
              <div class="rounded-md border border-line bg-primary-soft p-4" role="alert">
                <p class="font-medium">
                  Ce ticket a été modifié par quelqu’un d’autre. La dernière version a été
                  rechargée.
                </p>
                <p class="mt-1 text-sm">Votre modification : {{ c.summary }}.</p>
                <div class="mt-3 flex flex-wrap gap-2">
                  <button
                    type="button"
                    class="btn btn-secondary btn-small"
                    [attr.aria-disabled]="busy()"
                    (click)="reapply()"
                  >
                    Réappliquer ma modification
                  </button>
                  <button
                    type="button"
                    class="btn btn-secondary btn-small"
                    (click)="conflict.set(null)"
                  >
                    Ignorer
                  </button>
                </div>
              </div>
            }
            @if (actionError(); as message) {
              <p
                class="rounded-md border border-line bg-primary-soft p-3 text-sm font-medium"
                role="alert"
              >
                {{ message }}
              </p>
            }

            <div class="card">
              <h2 class="mb-2 text-lg font-semibold">Description</h2>
              <p class="whitespace-pre-line">{{ t.description }}</p>
            </div>

            @if (isAgent()) {
              <section class="card space-y-4" aria-labelledby="handling-title">
                <h2 id="handling-title" class="text-lg font-semibold">Traitement</h2>

                <div>
                  <p class="mb-1 text-sm font-medium">Statut : {{ statusLabels[t.status] }}</p>
                  @if (transitions().length === 0) {
                    <p class="text-sm text-ink-muted">
                      Aucune transition possible : le ticket est fermé.
                    </p>
                  } @else {
                    <ul class="flex flex-wrap gap-2" aria-label="Transitions possibles">
                      @for (next of transitions(); track next) {
                        <li>
                          <button
                            type="button"
                            class="btn btn-secondary"
                            [attr.aria-disabled]="busy()"
                            (click)="changeStatus(next)"
                          >
                            Passer à « {{ statusLabels[next] }} »
                          </button>
                        </li>
                      }
                    </ul>
                  }
                </div>

                <div class="grid gap-4 sm:grid-cols-3">
                  <div>
                    <label for="ticket-priority" class="mb-1 block text-sm font-medium">
                      Priorité
                    </label>
                    <select
                      id="ticket-priority"
                      class="field"
                      [attr.aria-disabled]="busy()"
                      (change)="changePriority($event)"
                    >
                      @for (priority of priorities; track priority) {
                        <option [value]="priority" [selected]="priority === t.priority">
                          {{ priorityLabels[priority] }}
                        </option>
                      }
                    </select>
                  </div>

                  <div>
                    <label for="ticket-assignee" class="mb-1 block text-sm font-medium">
                      Assigné à
                    </label>
                    <select
                      id="ticket-assignee"
                      class="field"
                      [attr.aria-disabled]="busy()"
                      (change)="changeAssignee($event)"
                    >
                      <option value="" [selected]="!t.assignee">Non assigné</option>
                      @for (assignee of assignees(); track assignee.id) {
                        <option [value]="assignee.id" [selected]="assignee.id === t.assignee?.id">
                          {{ assignee.displayName }}
                        </option>
                      }
                    </select>
                    @if (t.assignee?.id !== me()?.id) {
                      <button
                        type="button"
                        class="btn btn-secondary btn-small mt-2"
                        [attr.aria-disabled]="busy()"
                        (click)="assignToMe()"
                      >
                        M’assigner
                      </button>
                    }
                  </div>

                  <div>
                    <label for="ticket-category" class="mb-1 block text-sm font-medium">
                      Catégorie
                    </label>
                    <select
                      id="ticket-category"
                      class="field"
                      [attr.aria-disabled]="busy()"
                      (change)="changeCategory($event)"
                    >
                      <option value="" [selected]="!t.category">Aucune catégorie</option>
                      @for (category of categories(); track category.id) {
                        <option [value]="category.id" [selected]="category.id === t.category?.id">
                          {{ category.name }}
                        </option>
                      }
                    </select>
                  </div>
                </div>
              </section>
            }

            <section class="space-y-4" aria-labelledby="comments-title">
              <h2 id="comments-title" class="text-lg font-semibold">Échanges</h2>
              @if (t.comments.length === 0) {
                <p class="text-ink-muted">Aucun message pour le moment.</p>
              } @else {
                <ol class="space-y-3">
                  @for (comment of t.comments; track comment.id) {
                    <li
                      class="rounded-lg border p-4"
                      [class]="
                        comment.isInternal
                          ? 'border-dashed border-ink bg-primary-soft'
                          : 'border-line bg-card'
                      "
                      [attr.data-internal]="comment.isInternal"
                    >
                      <p class="flex flex-wrap items-center gap-2 text-sm">
                        <span class="font-medium">{{ comment.author.displayName }}</span>
                        <span [class]="comment.isInternal ? 'text-ink' : 'text-ink-muted'">
                          {{ comment.createdAt | date: 'dd/MM/yyyy HH:mm' }}
                        </span>
                        @if (comment.isInternal) {
                          <span class="badge badge-outline">
                            Note interne, invisible pour le demandeur
                          </span>
                        }
                      </p>
                      <p class="mt-2 whitespace-pre-line">{{ comment.body }}</p>
                    </li>
                  }
                </ol>
              }

              @if (t.status === 'Closed') {
                <p class="card text-ink-muted" role="status">
                  Ce ticket est fermé : il n’est plus possible d’y répondre. Créez un nouveau ticket
                  si le problème persiste.
                </p>
              } @else {
                <form
                  class="card space-y-3"
                  [formGroup]="replyForm"
                  (ngSubmit)="reply()"
                  novalidate
                >
                  <div>
                    <label for="reply-body" class="mb-1 block text-sm font-medium">
                      {{ isAgent() ? 'Répondre ou écrire une note' : 'Votre réponse' }}
                    </label>
                    <textarea
                      id="reply-body"
                      class="field"
                      rows="5"
                      maxlength="5000"
                      formControlName="body"
                    ></textarea>
                    <app-field-error [control]="replyForm.controls.body" />
                  </div>
                  @if (isAgent()) {
                    <label class="flex items-center gap-2 text-sm">
                      <input type="checkbox" formControlName="isInternal" class="size-5" />
                      Note interne (invisible pour le demandeur)
                    </label>
                  }
                  @if (replyError(); as message) {
                    <p
                      class="rounded-md border border-line bg-primary-soft p-3 text-sm font-medium"
                      role="alert"
                    >
                      {{ message }}
                    </p>
                  }
                  <button type="submit" class="btn btn-accent" [attr.aria-disabled]="busy()">
                    {{ replyForm.controls.isInternal.value ? 'Ajouter la note' : 'Envoyer' }}
                  </button>
                </form>
              }
            </section>

            <section class="card space-y-3" aria-labelledby="history-title">
              <h2 id="history-title" class="text-lg font-semibold">Historique</h2>
              @if (eventsError()) {
                <app-status-message
                  kind="error"
                  message="L’historique n’a pas pu être chargé."
                  (retry)="loadEvents()"
                />
              } @else if (events().length === 0) {
                <p class="text-ink-muted">Aucun événement.</p>
              } @else {
                <ol class="space-y-2 text-sm">
                  @for (event of events(); track event.id) {
                    <li class="border-l-2 border-line pl-3">
                      <span class="font-medium">{{ eventLabels[event.type] }}</span>
                      @if (event.type !== 'Created') {
                        : {{ display(event, event.oldValue) }} →
                        {{ display(event, event.newValue) }}
                      }
                      <span class="block text-ink-muted">
                        {{ event.actor?.displayName ?? 'Système' }} ·
                        {{ event.at | date: 'dd/MM/yyyy HH:mm' }}
                      </span>
                    </li>
                  }
                </ol>
              }
            </section>
          }
        }
      }
    </section>
  `,
})
export class TicketDetail implements OnInit {
  private readonly api = inject(ApiClient);
  private readonly auth = inject(AuthService);
  private readonly id = inject(ActivatedRoute).snapshot.paramMap.get('id') ?? '';

  protected readonly statusLabels = STATUS_LABELS;
  protected readonly priorityLabels = PRIORITY_LABELS;
  protected readonly priorities = PRIORITIES;
  protected readonly eventLabels = EVENT_LABELS;

  protected readonly replyForm = inject(NonNullableFormBuilder).group({
    body: ['', [Validators.required, Validators.maxLength(5000)]],
    isInternal: [false],
  });

  protected readonly status = signal<'loading' | 'ready' | 'notfound' | 'error'>('loading');
  protected readonly loadError = signal('');
  protected readonly ticket = signal<Ticket | null>(null);
  protected readonly events = signal<TicketEvent[]>([]);
  protected readonly eventsError = signal(false);
  protected readonly assignees = signal<UserRef[]>([]);
  protected readonly categories = signal<Category[]>([]);
  protected readonly busy = signal(false);
  protected readonly actionError = signal<string | null>(null);
  protected readonly replyError = signal<string | null>(null);
  protected readonly conflict = signal<Conflict | null>(null);
  /** Success announcement read by assistive technologies (role=status). */
  protected readonly notice = signal('');

  protected readonly me = this.auth.user;
  protected readonly isAgent = computed(() => isAgentRole(this.auth.role()));
  protected readonly transitions = computed(() => {
    const t = this.ticket();
    return t ? allowedTransitions(t.status) : [];
  });
  protected readonly breached = computed(() => {
    const t = this.ticket();
    return t ? isSlaBreachedDetail(t) : false;
  });

  /** A remote change arrived while an action was in flight: refresh once it is over. */
  private remoteChangePending = false;

  constructor() {
    inject(RealtimeService)
      .events.pipe(takeUntilDestroyed())
      .subscribe((event) => {
        if (event.ticketId !== this.id || this.status() !== 'ready') return;
        if (this.busy()) this.remoteChangePending = true;
        else void this.refreshQuietly();
      });
  }

  ngOnInit(): void {
    void this.load();
    void this.loadOptions();
  }

  async load(): Promise<void> {
    this.status.set('loading');
    try {
      this.ticket.set(await firstValueFrom(this.api.getTicket(this.id)));
      this.status.set('ready');
      void this.loadEvents();
    } catch (err) {
      if (err instanceof ApiError && (err.status === 404 || err.status === 403)) {
        this.status.set('notfound');
        return;
      }
      this.loadError.set(errorMessage(err));
      this.status.set('error');
    }
  }

  async loadEvents(): Promise<void> {
    try {
      this.events.set(await firstValueFrom(this.api.listTicketEvents(this.id)));
      this.eventsError.set(false);
    } catch {
      this.eventsError.set(true);
    }
  }

  /** Statuses and priorities come as codes; assignee and category events carry display names. */
  protected display(event: TicketEvent, value: string | null | undefined): string {
    if (!value) return '—';
    if (event.type === 'StatusChanged') {
      return STATUS_LABELS[value as keyof typeof STATUS_LABELS] ?? value;
    }
    if (event.type === 'PriorityChanged') {
      return PRIORITY_LABELS[value as keyof typeof PRIORITY_LABELS] ?? value;
    }
    return value;
  }

  protected changeStatus(next: Ticket['status']): Promise<boolean> {
    return this.apply({ status: next }, `passer à « ${STATUS_LABELS[next]} »`);
  }

  protected async changePriority(event: Event): Promise<void> {
    const select = event.target as HTMLSelectElement;
    const priority = select.value as Ticket['priority'];
    const ok = await this.apply({ priority }, `priorité « ${PRIORITY_LABELS[priority]} »`);
    if (!ok) select.value = this.ticket()?.priority ?? select.value;
  }

  protected async changeAssignee(event: Event): Promise<void> {
    const select = event.target as HTMLSelectElement;
    const assigneeId = select.value || null;
    const name = this.assignees().find((a) => a.id === assigneeId)?.displayName;
    const ok = await this.apply(
      { assigneeId },
      assigneeId ? `assigner à ${name ?? 'cet utilisateur'}` : 'retirer l’assignation',
    );
    if (!ok) select.value = this.ticket()?.assignee?.id ?? '';
  }

  protected assignToMe(): Promise<boolean> {
    const id = this.auth.user()?.id;
    return id ? this.apply({ assigneeId: id }, 'vous assigner le ticket') : Promise.resolve(false);
  }

  protected async changeCategory(event: Event): Promise<void> {
    const select = event.target as HTMLSelectElement;
    const categoryId = select.value || null;
    const name = this.categories().find((c) => c.id === categoryId)?.name;
    const ok = await this.apply(
      { categoryId },
      categoryId ? `catégorie « ${name ?? ''} »` : 'retirer la catégorie',
    );
    if (!ok) select.value = this.ticket()?.category?.id ?? '';
  }

  protected async reapply(): Promise<void> {
    const pending = this.conflict();
    if (!pending || this.busy()) return;
    this.conflict.set(null);
    await this.apply(pending.change, pending.summary);
  }

  protected async reply(): Promise<void> {
    if (this.busy()) return;
    this.replyError.set(null);
    this.notice.set('');
    if (this.replyForm.invalid) {
      this.replyForm.markAllAsTouched();
      return;
    }
    const { body, isInternal } = this.replyForm.getRawValue();
    this.busy.set(true);
    try {
      await firstValueFrom(
        this.api.addComment(this.id, {
          body: body.trim(),
          // Only agents may write internal notes; the box is not even shown to a Requester.
          isInternal: this.isAgent() && isInternal,
        }),
      );
      this.replyForm.reset({ body: '', isInternal: false });
      this.notice.set('Réponse envoyée.');
      await this.refresh();
    } catch (err) {
      if (err instanceof ApiError && err.status === 422 && applyFieldErrors(this.replyForm, err)) {
        return;
      }
      this.replyError.set(
        err instanceof ApiError && err.status === 409
          ? 'Ce ticket est fermé : la réponse n’a pas été envoyée.'
          : errorMessage(err),
      );
      if (err instanceof ApiError && err.status === 409) await this.refresh();
    } finally {
      this.busy.set(false);
      this.flushRemoteChange();
    }
  }

  /**
   * Sends a PATCH with the current rowVersion. Returns whether it was applied. On 409 the ticket
   * is reloaded: a version conflict offers to re-apply the change, an invalid transition explains.
   */
  private async apply(change: TicketChange, summary: string): Promise<boolean> {
    const current = this.ticket();
    if (!current || this.busy()) return false;
    this.notice.set('');
    this.actionError.set(null);
    this.conflict.set(null);
    this.busy.set(true);
    try {
      const updated = await firstValueFrom(
        this.api.updateTicket(this.id, { ...change, rowVersion: current.rowVersion }),
      );
      this.ticket.set(updated);
      this.notice.set('Ticket mis à jour.');
      void this.loadEvents();
      return true;
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        const type = err.problem?.type ?? '';
        await this.refresh();
        if (type.endsWith(PROBLEM_INVALID_TRANSITION)) {
          this.actionError.set(
            `Cette transition n’est pas autorisée depuis le statut « ${STATUS_LABELS[(this.ticket() ?? current).status]} ». Le ticket a été rechargé.`,
          );
        } else if (type.endsWith(PROBLEM_CONCURRENCY_CONFLICT)) {
          this.conflict.set({ change, summary });
        } else {
          this.actionError.set(`${err.message} Le ticket a été rechargé.`);
        }
      } else {
        this.actionError.set(errorMessage(err));
      }
      return false;
    } finally {
      this.busy.set(false);
      this.flushRemoteChange();
    }
  }

  /**
   * Refreshes ticket (so rowVersion stays current) and history after a remote event. Unlike
   * refresh(), it shows no error and never touches the reply form, notice or pending conflict.
   */
  private async refreshQuietly(): Promise<void> {
    try {
      this.ticket.set(await firstValueFrom(this.api.getTicket(this.id)));
      await this.loadEvents();
    } catch {
      // The ticket on screen stays valid; a 409 on the next change reloads it anyway.
    }
  }

  /** Reloads ticket and history without flashing the loading state. */
  private async refresh(): Promise<void> {
    try {
      this.ticket.set(await firstValueFrom(this.api.getTicket(this.id)));
    } catch (err) {
      this.actionError.set(errorMessage(err));
    }
    await this.loadEvents();
  }

  private flushRemoteChange(): void {
    if (!this.remoteChangePending) return;
    this.remoteChangePending = false;
    void this.refreshQuietly();
  }

  private async loadOptions(): Promise<void> {
    try {
      this.categories.set(await firstValueFrom(this.api.listCategories()));
    } catch {
      this.categories.set([]);
    }
    if (this.isAgent()) {
      try {
        this.assignees.set(await firstValueFrom(this.api.listAssignees()));
      } catch {
        this.assignees.set([]);
      }
    }
  }
}
