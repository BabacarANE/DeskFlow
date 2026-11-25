import { Page, expect, test } from '@playwright/test';
import { demoToken, newSession } from './helpers';

const DEFAULTS = {
  '--color-primary': '#7cc4ee',
  '--color-surface': '#f4f7fa',
  '--color-accent': '#d63a14',
};
const CUSTOM = {
  '--color-primary': '#2e7d32',
  '--color-surface': '#fafaf5',
  '--color-accent': '#6a1b9a',
};

/** Value of a CSS variable on :root, as the browser computes it. */
const rootVar = (page: Page, name: string) =>
  page.evaluate(
    (n) => getComputedStyle(document.documentElement).getPropertyValue(n).trim().toLowerCase(),
    name,
  );

test.describe('company theme', () => {
  // The theme belongs to the shared demo tenant: always give it back, even when the test fails.
  test.afterAll(async ({ request }) => {
    const token = await demoToken(request, 'admin');
    await request.delete('/api/v1/theme', { headers: { Authorization: `Bearer ${token}` } });
  });

  test('an admin changes the colours without reload, then resets them', async ({ browser }) => {
    const { context, page } = await newSession(browser, 'admin');
    try {
      await page.goto('/admin');
      await page.getByRole('tab', { name: 'Thème' }).click();
      await expect(
        page.getByText('Choisissez deux couleurs discrètes et une couleur qui claque.'),
      ).toBeVisible();

      // Survives only if the page is never reloaded.
      await page.evaluate(() => ((window as unknown as { __e2e: number }).__e2e = 1));

      await page
        .getByLabel('Code hexadécimal : Couleur principale')
        .fill(CUSTOM['--color-primary']);
      await page.getByLabel('Code hexadécimal : Couleur de fond').fill(CUSTOM['--color-surface']);
      await page.getByLabel('Code hexadécimal : Couleur d’accent').fill(CUSTOM['--color-accent']);
      await page.getByRole('button', { name: 'Enregistrer le thème' }).click();

      for (const [name, value] of Object.entries(CUSTOM)) {
        await expect.poll(() => rootVar(page, name)).toBe(value);
      }
      // The text colour on the primary colour follows the contrast rule (dark green: white text).
      await expect.poll(() => rootVar(page, '--on-primary')).not.toBe('');
      expect(await page.evaluate(() => (window as unknown as { __e2e?: number }).__e2e)).toBe(1);

      await page.getByRole('button', { name: 'Réinitialiser' }).click();
      await page.getByRole('button', { name: 'Confirmer la réinitialisation' }).click();

      for (const [name, value] of Object.entries(DEFAULTS)) {
        await expect.poll(() => rootVar(page, name)).toBe(value);
      }
      expect(await page.evaluate(() => (window as unknown as { __e2e?: number }).__e2e)).toBe(1);
    } finally {
      await context.close();
    }
  });
});
