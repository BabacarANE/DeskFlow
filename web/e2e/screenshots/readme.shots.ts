import { Page, expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { MAILPIT_URL, demoLogin } from '../helpers';

/**
 * Captures the README screenshots (1440x900) into docs/img. Meant to run against the stack with the
 * rich demo seeder (12 articles, 30 tickets): `npm run e2e:screenshots`. Not part of `npm run e2e`.
 */
const OUT = resolve(__dirname, '../../../docs/img');

async function shot(page: Page, name: string): Promise<void> {
  // Every list shows a loading state first: wait until none is left, then let web fonts settle.
  await expect(page.getByRole('status').filter({ hasText: /Chargement/ })).toHaveCount(0);
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({ path: resolve(OUT, `${name}.png`) });
}

test.describe.serial('README screenshots', () => {
  test.beforeAll(() => mkdirSync(OUT, { recursive: true }));

  test('login', async ({ page }) => {
    await page.goto('/login');
    await expect(page.getByRole('heading', { name: 'Connexion' })).toBeVisible();
    await shot(page, 'login');
  });

  test('tickets list, ticket detail, knowledge base', async ({ page }) => {
    await demoLogin(page, 'agent');

    await page.goto('/tickets');
    await expect(page.getByRole('heading', { level: 1, name: /Tickets/ })).toBeVisible();
    await expect(page.getByRole('table').getByRole('link').first()).toBeVisible();
    await shot(page, 'tickets-list');

    await page.getByRole('table').getByRole('link').first().click();
    await expect(page.getByRole('heading', { name: 'Échanges' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Historique' })).toBeVisible();
    await shot(page, 'ticket-detail');

    await page.goto('/kb');
    await expect(
      page.getByRole('heading', { level: 1, name: 'Base de connaissances' }),
    ).toBeVisible();
    await expect(
      page
        .getByRole('listitem')
        .filter({ has: page.getByRole('heading', { level: 2 }) })
        .first(),
    ).toBeVisible();
    await shot(page, 'knowledge-base');
  });

  test('admin theme with live preview', async ({ page }) => {
    await demoLogin(page, 'admin');
    await page.goto('/admin');
    await page.getByRole('tab', { name: 'Thème' }).click();
    await expect(page.getByRole('heading', { name: 'Aperçu en direct' })).toBeVisible();
    await shot(page, 'admin-theme');
  });

  test('Mailpit acknowledgement e-mail', async ({ page }) => {
    await page.goto(MAILPIT_URL);
    await page.getByText('Votre demande a bien été reçue').first().click();
    await expect(page.frameLocator('#preview-html').locator('body')).not.toBeEmpty();
    await page.screenshot({ path: resolve(OUT, 'mailpit.png') });
  });
});
