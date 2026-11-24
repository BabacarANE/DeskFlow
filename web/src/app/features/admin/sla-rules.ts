import { TicketPriority } from '../../core/api/models';

/** Bounds of the contract (SlaPolicy). */
export const SLA_MIN = 1;
export const SLA_MAX = 43_200;

/** Display order of the API: Low, Normal, High, Urgent. */
export const SLA_PRIORITIES: readonly TicketPriority[] = ['Low', 'Normal', 'High', 'Urgent'];

/** Readable duration of a number of minutes: "30 min", "4 h", "1 h 30 min", "3 j". */
export function formatMinutes(minutes: number | null | undefined): string {
  if (minutes === null || minutes === undefined || !Number.isFinite(minutes) || minutes < 0) {
    return '';
  }
  const total = Math.round(minutes);
  const days = Math.floor(total / 1440);
  const hours = Math.floor((total % 1440) / 60);
  const mins = total % 60;
  const parts: string[] = [];
  if (days) parts.push(`${days} j`);
  if (hours) parts.push(`${hours} h`);
  if (mins || parts.length === 0) parts.push(`${mins} min`);
  return parts.join(' ');
}

/** Validation message of one minutes value, or null when it is valid. */
export function minutesError(value: number | null | undefined): string | null {
  if (value === null || value === undefined || Number.isNaN(value)) {
    return 'Saisissez un nombre de minutes.';
  }
  if (!Number.isInteger(value)) return 'Saisissez un nombre entier de minutes.';
  if (value < SLA_MIN) return `La valeur minimale est ${SLA_MIN} minute.`;
  if (value > SLA_MAX) return `La valeur maximale est ${SLA_MAX} minutes (30 jours).`;
  return null;
}

/** The resolution can not be shorter than the first response. */
export function resolutionError(
  firstResponse: number | null | undefined,
  resolution: number | null | undefined,
): string | null {
  const own = minutesError(resolution);
  if (own) return own;
  if (
    firstResponse !== null &&
    firstResponse !== undefined &&
    resolution !== null &&
    resolution !== undefined &&
    resolution < firstResponse
  ) {
    return 'La résolution ne peut pas être plus courte que la première réponse.';
  }
  return null;
}

export interface SlaServerErrors {
  /** Row index then field name to message. */
  fields: Map<string, string>;
  /** Messages that match no field of the table. */
  general: string[];
}

const ROW_FIELD = /^policies(?:\[(\d+)\]|\.(\d+))\.(firstResponseMinutes|resolutionMinutes)$/i;

/**
 * Maps the `errors` of a 422 onto table cells. Keys look like `policies[2].resolutionMinutes`
 * (the dotted form is accepted too); anything else is returned as a general message.
 */
export function mapSlaServerErrors(errors: Record<string, string[]>): SlaServerErrors {
  const fields = new Map<string, string>();
  const general: string[] = [];
  for (const [key, messages] of Object.entries(errors)) {
    const match = ROW_FIELD.exec(key);
    if (!match) {
      general.push(...messages);
      continue;
    }
    const field = match[3].toLowerCase().startsWith('first') ? 'first' : 'resolution';
    fields.set(`${match[1] ?? match[2]}:${field}`, messages.join(' '));
  }
  return { fields, general };
}
