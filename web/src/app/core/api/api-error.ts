import { HttpErrorResponse } from '@angular/common/http';
import { ProblemDetails } from './models';

/** Normalised API failure, built by the error interceptor from a ProblemDetails body. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
    readonly problem: ProblemDetails | null,
    readonly fieldErrors: Record<string, string[]> = {},
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

export function messageForStatus(status: number): string {
  if (status === 0) return 'Impossible de joindre le serveur. Vérifiez votre connexion.';
  if (status === 401) return 'Votre session a expiré. Reconnectez-vous.';
  if (status === 403) return 'Vous n’avez pas le droit d’effectuer cette action.';
  if (status === 404) return 'Élément introuvable.';
  if (status === 409) return 'Cette opération entre en conflit avec l’état actuel des données.';
  if (status === 422) return 'Certaines informations sont invalides.';
  if (status === 429) return 'Trop de tentatives. Réessayez dans un instant.';
  if (status >= 500) return 'Le serveur a rencontré une erreur. Réessayez plus tard.';
  return 'Une erreur inattendue est survenue.';
}

export function toApiError(err: HttpErrorResponse): ApiError {
  const body = err.error as unknown;
  const problem =
    body && typeof body === 'object' && !Array.isArray(body) ? (body as ProblemDetails) : null;
  return new ApiError(err.status, messageForStatus(err.status), problem, problem?.errors ?? {});
}

export function errorMessage(err: unknown): string {
  return err instanceof Error ? err.message : 'Une erreur inattendue est survenue.';
}
