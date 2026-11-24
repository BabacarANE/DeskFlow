import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../api/api-client';
import { ApiError } from '../api/api-error';
import { errorInterceptor } from '../api/error.interceptor';
import {
  mockBackendInterceptor,
  resetMockBackend,
  setMockLatency,
} from './mock-backend.interceptor';

/** The simulated backend must follow the phase 4 contract: roles, search, 404 and 422. */
describe('mock backend (knowledge base, theme, SLA, categories)', () => {
  let api: ApiClient;
  let style: HTMLStyleElement;

  beforeEach(() => {
    sessionStorage.clear();
    setMockLatency(0);
    resetMockBackend();
    // The palette defaults come from tokens.css in the real application.
    style = document.createElement('style');
    style.textContent =
      ':root { --color-primary: #7cc4ee; --color-surface: #f4f7fa; --color-accent: #d63a14; }';
    document.head.appendChild(style);
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([errorInterceptor, mockBackendInterceptor]))],
    });
    api = TestBed.inject(ApiClient);
  });

  afterEach(() => {
    style.remove();
    sessionStorage.clear();
    setMockLatency(250);
  });

  const status = async (promise: Promise<unknown>) => {
    try {
      await promise;
      return 200;
    } catch (e) {
      return (e as ApiError).status;
    }
  };
  const failure = async (promise: Promise<unknown>) => {
    try {
      await promise;
    } catch (e) {
      return e as ApiError;
    }
    throw new Error('The request should have failed.');
  };
  const asAdmin = () => firstValueFrom(api.demoLogin('admin'));
  const asAgent = () => firstValueFrom(api.demoLogin('agent'));
  const asRequester = () => firstValueFrom(api.demoLogin('demandeur'));
  const request = (title = 'Titre valide') => ({
    title,
    symptom: 'Un symptôme',
    cause: '',
    solution: 'Une solution',
    tags: ['vpn'],
  });

  describe('articles', () => {
    it('shows only published articles to a requester, whatever the status parameter', async () => {
      await asRequester();
      const page = await firstValueFrom(api.listArticles({ status: 'Draft', pageSize: 100 }));
      expect(page.items.length).toBeGreaterThan(0);
      expect(page.items.every((a) => a.status === 'Published')).toBe(true);
    });

    it('lists published then drafts for an agent and filters by status', async () => {
      await asAgent();
      const all = await firstValueFrom(api.listArticles({ pageSize: 100 }));
      const statuses = all.items.map((a) => a.status);
      expect(statuses).toContain('Draft');
      expect(statuses.indexOf('Draft')).toBeGreaterThan(statuses.lastIndexOf('Published'));
      const drafts = await firstValueFrom(api.listArticles({ status: 'Draft' }));
      expect(drafts.items.every((a) => a.status === 'Draft')).toBe(true);
    });

    it('has a draft built from a ticket, with its sourceTicketId', async () => {
      await asAgent();
      const drafts = await firstValueFrom(api.listArticles({ status: 'Draft' }));
      const details = await Promise.all(
        drafts.items.map((a) => firstValueFrom(api.getArticle(a.id))),
      );
      expect(details.some((a) => a.sourceTicketId !== null)).toBe(true);
    });

    it('hides a draft from a requester with a 404', async () => {
      await asAgent();
      const draft = (await firstValueFrom(api.listArticles({ status: 'Draft' }))).items[0];
      await asRequester();
      expect(await status(firstValueFrom(api.getArticle(draft.id)))).toBe(404);
      expect(
        await status(firstValueFrom(api.getArticle('00000000-0000-4000-8000-0000000000ff'))),
      ).toBe(404);
    });

    it('searches every word, ignoring case and accents, title matches first', async () => {
      await asRequester();
      const found = await firstValueFrom(api.listArticles({ q: 'MESSAGERIE courriels' }));
      expect(found.items.map((a) => a.title)).toEqual([
        'La messagerie ne reçoit plus de courriels',
      ]);
      const accents = await firstValueFrom(api.listArticles({ q: 'mot de passe expire' }));
      expect(accents.items[0].title).toBe('Mot de passe expiré');
      // Words under 2 characters are ignored; an empty search lists everything.
      const ignored = await firstValueFrom(api.listArticles({ q: 'a vpn' }));
      expect(ignored.items[0].title).toBe('Le VPN refuse la connexion');
      const none = await firstValueFrom(api.listArticles({ q: 'zzzzqq' }));
      expect(none.total).toBe(0);
      const blank = await firstValueFrom(api.listArticles({ q: ' ' }));
      expect(blank.total).toBeGreaterThan(3);
    });

    it('paginates and refuses a search longer than 100 characters', async () => {
      await asRequester();
      const page = await firstValueFrom(api.listArticles({ pageSize: 2, page: 2 }));
      expect(page.items).toHaveLength(2);
      expect(page.page).toBe(2);
      expect(await status(firstValueFrom(api.listArticles({ q: 'x'.repeat(101) })))).toBe(422);
    });

    it('refuses article writes to a requester (403) and any access to the SuperAdmin', async () => {
      await asRequester();
      expect(await status(firstValueFrom(api.createArticle(request())))).toBe(403);
      sessionStorage.setItem('deskflow.mock.role', 'superadmin');
      expect(await status(firstValueFrom(api.listArticles()))).toBe(403);
    });

    it('creates a draft, validates fields with 422 and keeps the status on update', async () => {
      await asAgent();
      const created = await firstValueFrom(api.createArticle(request()));
      expect(created.status).toBe('Draft');
      expect(created.sourceTicketId).toBeNull();
      expect(created.author.displayName).toBe('Adam Agent');

      const bad = await failure(
        firstValueFrom(api.createArticle({ ...request('ab'), tags: ['a,b'] })),
      );
      expect(bad.status).toBe(422);
      expect(Object.keys(bad.fieldErrors).sort()).toEqual(['tags', 'title']);
      const many = Array.from({ length: 11 }, (_, i) => `t${i}`);
      expect(await status(firstValueFrom(api.createArticle({ ...request(), tags: many })))).toBe(
        422,
      );

      const updated = await firstValueFrom(
        api.updateArticle(created.id, { ...request('Nouveau titre'), tags: ['x', 'X', 'y'] }),
      );
      expect(updated.title).toBe('Nouveau titre');
      expect(updated.tags).toEqual(['x', 'y']);
      expect(updated.status).toBe('Draft');
    });

    it('publishes, is idempotent, refuses an incomplete article and unpublishes', async () => {
      await asAgent();
      const created = await firstValueFrom(api.createArticle(request()));
      const published = await firstValueFrom(api.publishArticle(created.id));
      expect(published.status).toBe('Published');
      expect(published.publishedAt).not.toBeNull();
      const again = await firstValueFrom(api.publishArticle(created.id));
      expect(again.publishedAt).toBe(published.publishedAt);
      const back = await firstValueFrom(api.unpublishArticle(created.id));
      expect(back.status).toBe('Draft');
      expect(back.publishedAt).toBeNull();

      const empty = await firstValueFrom(
        api.createArticle({ ...request(), symptom: '', solution: '' }),
      );
      const error = await failure(firstValueFrom(api.publishArticle(empty.id)));
      expect(error.status).toBe(422);
      expect(Object.keys(error.fieldErrors).sort()).toEqual(['solution', 'symptom']);
      // An empty cause is allowed.
      expect(created.cause).toBe('');
      expect((await firstValueFrom(api.publishArticle(created.id))).status).toBe('Published');
    });
  });

  describe('theme', () => {
    it('serves the palette defaults, then the saved theme in upper case through /me', async () => {
      await asAdmin();
      expect((await firstValueFrom(api.getTheme())).colorAccent).toBe('#d63a14');
      const result = await firstValueFrom(
        api.updateTheme({
          colorPrimary: '#aabbcc',
          colorSurface: '#fafafa',
          colorAccent: '#aa2200',
        }),
      );
      expect(result.theme).toEqual({
        colorPrimary: '#AABBCC',
        colorSurface: '#FAFAFA',
        colorAccent: '#AA2200',
      });
      expect(result.warnings).toEqual([]);
      const me = await firstValueFrom(api.getMe());
      expect(me.tenant?.theme.colorPrimary).toBe('#AABBCC');
      const reset = await firstValueFrom(api.resetTheme());
      expect(reset.colorPrimary).toBe('#7cc4ee');
    });

    it('warns without blocking when the accent has less than 3:1 against the surface', async () => {
      await asAdmin();
      const result = await firstValueFrom(
        api.updateTheme({
          colorPrimary: '#102030',
          colorSurface: '#f4f7fa',
          colorAccent: '#e8eef3',
        }),
      );
      expect(result.warnings).toHaveLength(1);
    });

    it('refuses malformed colours with a 422 per field and writes by non-admins', async () => {
      await asAdmin();
      const error = await failure(
        firstValueFrom(
          api.updateTheme({ colorPrimary: 'blue', colorSurface: '#F4F7FA', colorAccent: '#FFF' }),
        ),
      );
      expect(Object.keys(error.fieldErrors).sort()).toEqual(['colorAccent', 'colorPrimary']);
      await asAgent();
      expect((await firstValueFrom(api.getTheme())).colorSurface).toBe('#f4f7fa');
      const valid = { colorPrimary: '#102030', colorSurface: '#F4F7FA', colorAccent: '#D63A14' };
      expect(await status(firstValueFrom(api.updateTheme(valid)))).toBe(403);
      expect(await status(firstValueFrom(api.resetTheme()))).toBe(403);
    });
  });

  describe('SLA', () => {
    it('lists four rows in the API order and is reserved to the tenant admin', async () => {
      await asAdmin();
      const rows = await firstValueFrom(api.listSlaPolicies());
      expect(rows.map((r) => r.priority)).toEqual(['Low', 'Normal', 'High', 'Urgent']);
      expect(rows[3]).toEqual({
        priority: 'Urgent',
        firstResponseMinutes: 30,
        resolutionMinutes: 240,
      });
      await asAgent();
      expect(await status(firstValueFrom(api.listSlaPolicies()))).toBe(403);
    });

    it('saves valid rows and refuses resolution < first response with a 422 on the cell', async () => {
      await asAdmin();
      const rows = await firstValueFrom(api.listSlaPolicies());
      const saved = await firstValueFrom(
        api.updateSlaPolicies({ policies: rows.map((r) => ({ ...r, resolutionMinutes: 5000 })) }),
      );
      expect(saved.every((r) => r.resolutionMinutes === 5000)).toBe(true);

      const bad = rows.map((r, i) =>
        i === 2 ? { ...r, firstResponseMinutes: 600, resolutionMinutes: 100 } : r,
      );
      const error = await failure(firstValueFrom(api.updateSlaPolicies({ policies: bad })));
      expect(error.status).toBe(422);
      expect(Object.keys(error.fieldErrors)).toEqual(['policies[2].resolutionMinutes']);
      expect(
        await status(firstValueFrom(api.updateSlaPolicies({ policies: rows.slice(0, 3) }))),
      ).toBe(422);
    });
  });

  describe('categories', () => {
    it('creates, renames and deletes, with 409 on a duplicate name', async () => {
      await asAdmin();
      const created = await firstValueFrom(api.createCategory('Réseau'));
      expect(await status(firstValueFrom(api.createCategory('réseau')))).toBe(409);
      expect(await status(firstValueFrom(api.createCategory('  ')))).toBe(422);
      const renamed = await firstValueFrom(api.renameCategory(created.id, 'Réseau et VPN'));
      expect(renamed.name).toBe('Réseau et VPN');
      expect(await status(firstValueFrom(api.renameCategory(created.id, 'Matériel')))).toBe(409);
      expect(await status(firstValueFrom(api.renameCategory('nope', 'X')))).toBe(404);
      await firstValueFrom(api.deleteCategory(created.id));
      const names = (await firstValueFrom(api.listCategories())).map((c) => c.name);
      expect(names).not.toContain('Réseau et VPN');
    });

    it('refuses to delete a category used by tickets (409) and writes by non-admins (403)', async () => {
      await asAdmin();
      const [used] = await firstValueFrom(api.listCategories());
      expect(await status(firstValueFrom(api.deleteCategory(used.id)))).toBe(409);
      await asAgent();
      expect(await status(firstValueFrom(api.createCategory('Autre')))).toBe(403);
      expect((await firstValueFrom(api.listCategories())).length).toBeGreaterThan(0);
    });
  });
});
