import { expect, test } from '@playwright/test';

/**
 * The redirect URI sent to the provider must be the one declared in its console, port included
 * (docs/setup-oauth.md). Nothing is sent to Google or GitHub: only the 302 of the API is read.
 * Needs OAuth credentials in .env (dummy values in CI); skipped locally when a provider is off.
 */
for (const provider of ['google', 'github']) {
  test(`${provider} sign-in sends the public redirect URI, port included`, async ({
    request,
    baseURL,
  }) => {
    const response = await request.get(`/api/v1/auth/external/${provider}`, { maxRedirects: 0 });
    test.skip(response.status() === 404 && !process.env['CI'], `${provider} is not configured`);

    expect(response.status()).toBe(302);
    const location = new URL(response.headers()['location']);
    expect(location.searchParams.get('redirect_uri')).toBe(
      new URL(`/signin-${provider}`, baseURL).href,
    );
  });
}
