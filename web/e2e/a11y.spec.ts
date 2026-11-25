import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';
import { DemoRole, demoLogin } from './helpers';

/** No serious or critical WCAG A/AA violation on the main screens (SPEC section 10: accessibility). */
async function expectNoSeriousViolation(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
    .analyze();
  expect(results.passes.length, 'axe did run its rules').toBeGreaterThan(10);
  const serious = results.violations.filter(
    (v) => v.impact === 'serious' || v.impact === 'critical',
  );
  expect(
    serious.map((v) => ({
      id: v.id,
      impact: v.impact,
      help: v.help,
      targets: v.nodes.slice(0, 3).map((n) => n.target.join(' ')),
    })),
  ).toEqual([]);
}

test.describe('accessibility (axe-core)', () => {
  test('login', async ({ page }) => {
    await page.goto('/login');
    await expect(page.getByRole('heading', { name: 'Connexion' })).toBeVisible();
    await expectNoSeriousViolation(page);
  });

  const signedIn: { name: string; role: DemoRole; path: string; heading: string }[] = [
    { name: 'tickets list', role: 'agent', path: '/tickets', heading: 'Tickets' },
    { name: 'knowledge base', role: 'agent', path: '/kb', heading: 'Base de connaissances' },
    { name: 'admin', role: 'admin', path: '/admin', heading: 'Administration' },
  ];

  for (const { name, role, path, heading } of signedIn) {
    test(name, async ({ page }) => {
      await demoLogin(page, role);
      await page.goto(path);
      await expect(
        page.getByRole('heading', { level: 1, name: new RegExp(heading) }),
      ).toBeVisible();
      await expect(page.getByRole('status').filter({ hasText: /Chargement/ })).toHaveCount(0);
      await expectNoSeriousViolation(page);
    });
  }

  test('admin, theme tab (colour pickers and live preview)', async ({ page }) => {
    await demoLogin(page, 'admin');
    await page.goto('/admin');
    await page.getByRole('tab', { name: 'Thème' }).click();
    await expect(page.getByRole('heading', { name: 'Aperçu en direct' })).toBeVisible();
    await expectNoSeriousViolation(page);
  });
});
