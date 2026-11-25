import { APIRequestContext, Browser, BrowserContext, Page, expect } from '@playwright/test';

export type DemoRole = 'admin' | 'agent' | 'demandeur';

export const MAILPIT_URL = process.env['E2E_MAILPIT_URL'] ?? 'http://localhost:8025';

const ROLE_LABEL: Record<DemoRole, RegExp> = {
  admin: /^Administrateur/,
  agent: /^Agent/,
  demandeur: /^Demandeur/,
};

/** Signs in through the login screen, like a user: choose the profile, press "Essayer la démo". */
export async function demoLogin(page: Page, role: DemoRole): Promise<void> {
  await page.goto('/login');
  await page.getByRole('radio', { name: ROLE_LABEL[role] }).check();
  await page.getByRole('button', { name: 'Essayer la démo' }).click();
  await expect(page.getByRole('button', { name: 'Se déconnecter' })).toBeVisible();
}

/** A fresh browser context (own cookies): each role has its own session. */
export async function newSession(
  browser: Browser,
  role: DemoRole,
): Promise<{ context: BrowserContext; page: Page }> {
  const context = await browser.newContext();
  const page = await context.newPage();
  await demoLogin(page, role);
  return { context, page };
}

/** Title unique per run, so the tests never depend on (or collide with) the seeded data. */
export function uniqueTitle(label: string): string {
  return `E2E ${label} ${Date.now().toString(36)}`;
}

/** Access token of a demo profile, for API clean-up that does not belong to the scenario. */
export async function demoToken(request: APIRequestContext, role: DemoRole): Promise<string> {
  const response = await request.post('/api/v1/auth/demo', { data: { role } });
  expect(response.ok()).toBeTruthy();
  return ((await response.json()) as { accessToken: string }).accessToken;
}

interface MailpitMessage {
  ID: string;
  Subject: string;
  Created: string;
  To: { Address: string }[];
}

/** Messages of the Mailpit inbox received by `to` whose subject contains `subjectPart`. */
export async function findMails(
  request: APIRequestContext,
  to: string,
  subjectPart: string,
): Promise<MailpitMessage[]> {
  const response = await request.get(`${MAILPIT_URL}/api/v1/messages?limit=200`);
  expect(response.ok()).toBeTruthy();
  const body = (await response.json()) as { messages: MailpitMessage[] };
  return body.messages.filter(
    (m) => m.Subject.includes(subjectPart) && m.To.some((t) => t.Address === to),
  );
}
