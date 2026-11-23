export const environment = {
  /** No OAuth in mock mode: the buttons lead to the callback error screens instead. */
  mockExternalLoginUrl: ((invitation?: string | null): string =>
    `/auth/callback?error=${invitation ? 'invitation_invalid' : 'invitation_required'}`) as
    | ((invitation?: string | null) => string)
    | null,
};
