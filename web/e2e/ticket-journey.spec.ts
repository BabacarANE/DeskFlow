import { Page, expect, test } from '@playwright/test';
import { findMails, newSession, uniqueTitle } from './helpers';

const REQUESTER_EMAIL = 'demandeur@demo.deskflow.example';

/**
 * SPEC section 11 / 15: demo sign-in, ticket creation, acknowledgement e-mail in Mailpit in under
 * 5 seconds, agent reply, resolution, knowledge base draft. One serial scenario; it creates its own
 * ticket, so it passes before and after the rich demo seeder.
 */
test.describe.serial('ticket journey', () => {
  const title = uniqueTitle('Imprimante');
  const description = 'Le bac 2 de l’imprimante du 3e étage reste bloqué depuis ce matin.';
  const answer = `Réinitialisez le bac 2 depuis le panneau, puis relancez l’impression (${title}).`;

  let ticketUrl = '';
  let ticketNumber = '';

  let requester: Page;
  let agent: Page;

  test.afterAll(async () => {
    await requester?.context().close();
    await agent?.context().close();
  });

  test('the requester creates a ticket and gets the acknowledgement e-mail within 5 seconds', async ({
    browser,
    request,
  }) => {
    ({ page: requester } = await newSession(browser, 'demandeur'));

    await requester.getByRole('link', { name: 'Nouveau ticket' }).first().click();
    await expect(requester.getByRole('heading', { name: 'Nouveau ticket' })).toBeVisible();

    await requester.getByLabel('Titre').fill(title);
    await requester.getByLabel('Description').fill(description);
    await requester.getByLabel('Priorité').selectOption({ label: 'Haute' });

    const clicked = Date.now();
    await requester.getByRole('button', { name: 'Créer le ticket' }).click();

    await expect(requester).toHaveURL(/\/tickets\/[0-9a-f-]{36}$/);
    ticketUrl = new URL(requester.url()).pathname;
    const heading = requester.getByRole('heading', { level: 1 });
    await expect(heading).toContainText(title);
    ticketNumber = ((await heading.textContent()) ?? '').match(/#(\d+)/)?.[1] ?? '';
    expect(ticketNumber).not.toBe('');

    await expect
      .poll(async () => (await findMails(request, REQUESTER_EMAIL, `[#${ticketNumber}]`)).length, {
        timeout: 5_000,
        intervals: [250],
        message: 'acknowledgement e-mail in Mailpit',
      })
      .toBeGreaterThan(0);
    expect(Date.now() - clicked).toBeLessThan(5_000);

    const [mail] = await findMails(request, REQUESTER_EMAIL, `[#${ticketNumber}]`);
    expect(mail.Subject).toContain('Votre demande a bien été reçue');
  });

  test('the ticket appears in the requester list', async () => {
    await requester.goto('/tickets');
    await expect(requester.getByRole('link', { name: new RegExp(title) })).toBeVisible();
  });

  test('an agent assigns the ticket, answers publicly and resolves it', async ({ browser }) => {
    ({ page: agent } = await newSession(browser, 'agent'));
    await agent.goto(ticketUrl);
    await expect(agent.getByRole('heading', { level: 1 })).toContainText(title);

    // Assign to self.
    await agent.getByRole('button', { name: /M.assigner/ }).click();
    await expect(agent.getByLabel('Assigné à')).not.toHaveValue('');

    // Public answer (the internal-note box stays unchecked).
    await agent.getByLabel('Répondre ou écrire une note').fill(answer);
    await expect(agent.getByLabel(/Note interne/)).not.toBeChecked();
    await agent.getByRole('button', { name: 'Envoyer', exact: true }).click();
    await expect(agent.getByRole('listitem').filter({ hasText: answer })).toBeVisible();

    // New -> Open (when the assignment did not already open it) -> Resolved.
    const open = agent.getByRole('button', { name: 'Passer à « Ouvert »' });
    const alreadyOpen = agent.getByText('Statut : Ouvert');
    // Wait for the page to settle on one of the two states before branching (the assignment may open it).
    await expect(open.or(alreadyOpen)).toBeVisible();
    if (await open.isVisible()) {
      await open.click();
      await expect(alreadyOpen).toBeVisible();
    }
    await agent.getByRole('button', { name: 'Passer à « Résolu »' }).click();
    await expect(agent.getByText('Statut : Résolu')).toBeVisible();
  });

  test('the requester sees the public answer', async () => {
    await requester.goto(ticketUrl);
    await expect(requester.getByRole('listitem').filter({ hasText: answer })).toBeVisible();
  });

  test('resolving created a draft article, visible to the agent in /kb', async () => {
    await agent.goto('/kb');
    await agent.getByLabel('Statut').selectOption('Draft');
    await agent.getByLabel('Rechercher', { exact: true }).fill(title);
    await agent.getByRole('button', { name: 'Rechercher' }).click();

    // The draft is created by the worker after ticket.resolved: wait, do not sleep.
    const draft = agent.getByRole('link', { name: title });
    await expect(async () => {
      await agent.getByRole('button', { name: 'Rechercher' }).click();
      await expect(draft).toBeVisible({ timeout: 1_000 });
    }).toPass({ timeout: 20_000, intervals: [500, 1_000] });

    await expect(
      agent.getByRole('listitem').filter({ has: draft }).getByText('Brouillon'),
    ).toBeVisible();

    await draft.click();
    await expect(agent.getByRole('heading', { level: 1, name: title })).toBeVisible();
    await expect(agent.getByText(description)).toBeVisible();
    await expect(agent.getByText(answer)).toBeVisible();
  });

  test('a requester has no draft filter and does not see the draft', async () => {
    await requester.goto('/kb');
    await expect(requester.getByRole('heading', { name: 'Base de connaissances' })).toBeVisible();
    await expect(requester.getByLabel('Statut')).toHaveCount(0);
    await expect(requester.getByRole('link', { name: title })).toHaveCount(0);
  });
});
