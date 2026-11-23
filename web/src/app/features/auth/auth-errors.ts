import { AUTH_ERROR_CODES, AuthErrorCode } from '../../core/api/models';

/** French messages for every `/auth/callback?error=<code>` of the contract. */
export const AUTH_ERROR_MESSAGES: Record<AuthErrorCode, string> = {
  invitation_required:
    'Aucun compte n’existe pour cette adresse. Pour une première connexion, utilisez le lien de l’invitation envoyée par l’administrateur de votre entreprise.',
  invitation_invalid:
    'Cette invitation est invalide, expirée ou déjà utilisée. Demandez-en une nouvelle à votre administrateur.',
  email_mismatch:
    'L’adresse e-mail du compte choisi ne correspond pas à celle de l’invitation. Reconnectez-vous avec le compte invité.',
  email_not_verified:
    'L’adresse e-mail de ce compte n’est pas vérifiée chez le fournisseur. Vérifiez-la, puis réessayez.',
  account_disabled: 'Votre compte est désactivé. Contactez l’administrateur de votre entreprise.',
  tenant_suspended:
    'L’accès de votre entreprise est suspendu. Contactez l’administrateur de la plateforme.',
};

export const UNKNOWN_AUTH_ERROR_MESSAGE =
  'La connexion a échoué pour une raison inconnue. Réessayez dans un instant.';

export function authErrorMessage(code: string): string {
  return (AUTH_ERROR_CODES as readonly string[]).includes(code)
    ? AUTH_ERROR_MESSAGES[code as AuthErrorCode]
    : UNKNOWN_AUTH_ERROR_MESSAGE;
}
