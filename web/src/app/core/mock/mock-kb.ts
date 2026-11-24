import {
  Article,
  ArticleRequest,
  ArticleStatus,
  ArticleSummary,
  Me,
  Page,
  Theme,
  ThemeUpdateResult,
  UserRef,
} from '../api/models';
import { isAgentRole } from '../api/ticket-rules';
import { ACCENT_CONTRAST_WARNING, accentContrastWarning, isHexColor } from '../theme/contrast';
import { MockContext, MockResult } from './mock-tickets';

/**
 * Knowledge base and theme part of the simulated backend. It follows the frozen phase 4 contract
 * (docs/api/openapi.json, docs/api/phase4.md): role rules, simple search, publish and unpublish,
 * and the same 404 and 422 answers as the API. Pure logic, like mock-tickets.
 */

const AUTHORS: Record<'adam' | 'lea', UserRef> = {
  adam: { id: '10000000-0000-4000-8000-000000000002', displayName: 'Adam Agent' },
  lea: { id: '10000000-0000-4000-8000-000000000004', displayName: 'Léa Martin' },
};

/** A resolved ticket of the mock ticket set: the draft built from it carries its id. */
const SOURCE_TICKET_ID = '40000000-0000-4000-8000-000000000005';

const TITLE_MIN = 3;
const TITLE_MAX = 200;
const SYMPTOM_MAX = 4000;
const CAUSE_MAX = 4000;
const SOLUTION_MAX = 8000;
const TAG_MAX_COUNT = 10;
const TAG_MAX_LENGTH = 30;
const Q_MAX = 100;

interface KbState {
  articles: Article[];
  theme: Theme | null;
  nextId: number;
}

function problem(status: number, title: string, errors?: Record<string, string[]>): MockResult {
  return { status, problem: { title, ...(errors ? { errors } : {}) } };
}

function daysAgo(days: number): string {
  return new Date(Date.now() - days * 86_400_000).toISOString();
}

function article(
  n: number,
  title: string,
  symptom: string,
  cause: string,
  solution: string,
  tags: string[],
  status: ArticleStatus,
  ageDays: number,
  extra: Partial<Article> = {},
): Article {
  return {
    id: `70000000-0000-4000-8000-${String(n).padStart(12, '0')}`,
    title,
    symptom,
    cause,
    solution,
    tags,
    status,
    sourceTicketId: null,
    author: n % 2 === 0 ? AUTHORS.adam : AUTHORS.lea,
    createdAt: daysAgo(ageDays + 1),
    publishedAt: status === 'Published' ? daysAgo(ageDays) : null,
    ...extra,
  };
}

function seed(): KbState {
  const articles: Article[] = [
    article(
      1,
      'Le VPN refuse la connexion',
      'Le client VPN affiche « Échec de l’authentification » ou reste bloqué sur « Connexion en cours ».',
      'Le mot de passe a expiré, ou le certificat de l’appareil n’est plus valide.',
      '1. Fermez puis rouvrez le client VPN.\n2. Vérifiez que votre mot de passe fonctionne sur la messagerie.\n3. Si l’erreur persiste, ouvrez un ticket en indiquant l’heure de l’échec.',
      ['vpn', 'réseau', 'accès'],
      'Published',
      3,
    ),
    article(
      2,
      'Imprimante hors ligne',
      'L’imprimante apparaît « Hors ligne » et les documents restent dans la file d’attente.',
      'L’imprimante a perdu son adresse réseau après une coupure de courant.',
      '1. Éteignez l’imprimante, attendez dix secondes, rallumez-la.\n2. Supprimez les documents bloqués de la file d’attente.\n3. Relancez l’impression.',
      ['imprimante', 'matériel'],
      'Published',
      6,
    ),
    article(
      3,
      'Mot de passe expiré',
      'Une fenêtre indique que le mot de passe a expiré et la session ne s’ouvre plus.',
      'Les mots de passe doivent être renouvelés tous les 90 jours.',
      'Utilisez le lien « Mot de passe oublié » de la page de connexion, puis choisissez un mot de passe différent des trois précédents.',
      ['mot de passe', 'accès', 'compte'],
      'Published',
      9,
    ),
    article(
      4,
      'La messagerie ne reçoit plus de courriels',
      'Aucun nouveau message n’arrive depuis plusieurs heures, alors que l’envoi fonctionne.',
      'La boîte aux lettres est pleine ou la synchronisation est suspendue.',
      'Videz le dossier « Éléments supprimés », puis redémarrez l’application de messagerie. Si le quota est atteint, archivez les anciens messages.',
      ['messagerie', 'courriel'],
      'Published',
      12,
    ),
    article(
      5,
      'Accéder à un partage réseau',
      'Le message « Chemin réseau introuvable » s’affiche à l’ouverture du dossier partagé.',
      'Le lecteur réseau n’est pas monté, ou le VPN est déconnecté hors du bureau.',
      '1. Connectez-vous au VPN si vous êtes à distance.\n2. Ouvrez l’explorateur de fichiers et reconnectez le lecteur réseau.\n3. Demandez l’accès au dossier à votre responsable si le refus persiste.',
      ['partage', 'réseau', 'accès'],
      'Published',
      15,
    ),
    article(
      6,
      'Wi-Fi instable en salle de réunion',
      'La connexion sans fil se coupe toutes les quelques minutes en salle de réunion.',
      'La borne de la salle est saturée aux heures de forte affluence.',
      'Connectez-vous au réseau « Invités » ou branchez-vous au câble de la salle. Signalez l’heure de la coupure pour l’équipe réseau.',
      ['wi-fi', 'réseau'],
      'Published',
      20,
    ),
    article(
      7,
      'Écran externe non détecté',
      'Le second écran reste noir quand on branche le câble.',
      '',
      '',
      ['écran', 'matériel'],
      'Draft',
      1,
    ),
    article(
      8,
      'Imprimante du 2e étage hors service',
      'Bonjour, le problème dure depuis ce matin et bloque mon travail.',
      '',
      'Bien reçu, je regarde cela tout de suite.',
      ['imprimante'],
      'Draft',
      0,
      { sourceTicketId: SOURCE_TICKET_ID, author: AUTHORS.adam },
    ),
  ];
  return { articles, theme: null, nextId: 100 };
}

let state = seed();

/** Test helper: restores the initial articles and the default theme. */
export function resetMockKb(): void {
  state = seed();
}

/**
 * The locked palette, read from tokens.css (no hex outside that file). Any theme already written
 * inline on :root is set aside while reading, so the real defaults are returned.
 */
export function paletteDefaults(): Theme {
  const root = document.documentElement;
  const names = ['--color-primary', '--color-surface', '--color-accent'];
  const saved = names.map((name) => root.style.getPropertyValue(name));
  names.forEach((name) => root.style.removeProperty(name));
  const css = getComputedStyle(root);
  const read = (name: string) => css.getPropertyValue(name).trim();
  const theme: Theme = {
    colorPrimary: read('--color-primary'),
    colorSurface: read('--color-surface'),
    colorAccent: read('--color-accent'),
  };
  names.forEach((name, i) => {
    if (saved[i]) root.style.setProperty(name, saved[i]);
  });
  return theme;
}

/** Theme of the demo tenant: the saved one, or the palette defaults. */
export function currentMockTheme(): Theme {
  return state.theme ? { ...state.theme } : paletteDefaults();
}

function fold(text: string): string {
  return text.normalize('NFD').replace(/\p{M}/gu, '').toLowerCase();
}

function words(q: string): string[] {
  return fold(q)
    .split(/\s+/)
    .filter((w) => w.length >= 2);
}

function toSummary(a: Article): ArticleSummary {
  return {
    id: a.id,
    title: a.title,
    status: a.status,
    tags: [...a.tags],
    excerpt: a.symptom.slice(0, 200),
    createdAt: a.createdAt,
    publishedAt: a.publishedAt,
  };
}

function visibleTo(a: Article, me: Me): boolean {
  return isAgentRole(me.role) || a.status === 'Published';
}

function listArticles(ctx: MockContext): MockResult {
  const { query, me } = ctx;
  const page = Number(query.get('page') ?? 1);
  const pageSize = Number(query.get('pageSize') ?? 20);
  const status = query.get('status');
  const q = query.get('q') ?? '';
  const errors: Record<string, string[]> = {};
  if (!Number.isInteger(page) || page < 1) errors['page'] = ['La page doit être au moins 1.'];
  if (!Number.isInteger(pageSize) || pageSize < 1 || pageSize > 100) {
    errors['pageSize'] = ['La taille de page doit être comprise entre 1 et 100.'];
  }
  if (status && status !== 'Draft' && status !== 'Published')
    errors['status'] = ['Statut inconnu.'];
  if (q.length > Q_MAX) errors['q'] = [`La recherche ne peut pas dépasser ${Q_MAX} caractères.`];
  if (Object.keys(errors).length > 0) return problem(422, 'Validation failed', errors);

  let items = state.articles.filter((a) => visibleTo(a, me));
  // The status filter is ignored for a Requester (published only).
  if (isAgentRole(me.role) && status) items = items.filter((a) => a.status === status);

  const terms = words(q);
  const published = (a: Article) => (a.publishedAt ? Date.parse(a.publishedAt) : 0);
  if (terms.length > 0) {
    const titleMatches = new Set<string>();
    items = items.filter((a) => {
      const title = fold(a.title);
      const haystack = fold([a.title, a.symptom, a.cause, a.solution, ...a.tags].join('\n'));
      if (!terms.every((t) => haystack.includes(t))) return false;
      if (terms.every((t) => title.includes(t))) titleMatches.add(a.id);
      return true;
    });
    items = [...items].sort(
      (a, b) =>
        Number(titleMatches.has(b.id)) - Number(titleMatches.has(a.id)) ||
        published(b) - published(a),
    );
  } else {
    // Published by publication date, then the drafts by creation date, newest first.
    items = [...items].sort(
      (a, b) =>
        Number(b.status === 'Published') - Number(a.status === 'Published') ||
        (a.status === 'Published'
          ? published(b) - published(a)
          : Date.parse(b.createdAt) - Date.parse(a.createdAt)),
    );
  }

  const result: Page<ArticleSummary> = {
    items: items.slice((page - 1) * pageSize, page * pageSize).map(toSummary),
    total: items.length,
    page,
    pageSize,
  };
  return { status: 200, body: result };
}

function parseRequest(body: unknown): { value?: ArticleRequest; errors: Record<string, string[]> } {
  const req = (body ?? {}) as Partial<ArticleRequest>;
  const errors: Record<string, string[]> = {};
  const title = (req.title ?? '').trim();
  const symptom = (req.symptom ?? '').trim();
  const cause = (req.cause ?? '').trim();
  const solution = (req.solution ?? '').trim();
  if (title.length < TITLE_MIN || title.length > TITLE_MAX) {
    errors['title'] = [`Le titre doit comporter de ${TITLE_MIN} à ${TITLE_MAX} caractères.`];
  }
  if (symptom.length > SYMPTOM_MAX) {
    errors['symptom'] = [`Le symptôme ne peut pas dépasser ${SYMPTOM_MAX} caractères.`];
  }
  if (cause.length > CAUSE_MAX) {
    errors['cause'] = [`La cause ne peut pas dépasser ${CAUSE_MAX} caractères.`];
  }
  if (solution.length > SOLUTION_MAX) {
    errors['solution'] = [`La solution ne peut pas dépasser ${SOLUTION_MAX} caractères.`];
  }
  const rawTags = Array.isArray(req.tags) ? req.tags : [];
  const tags: string[] = [];
  for (const raw of rawTags) {
    const tag = String(raw).trim();
    if (tag.length < 1 || tag.length > TAG_MAX_LENGTH || tag.includes(',')) {
      errors['tags'] = [
        `Chaque étiquette comporte 1 à ${TAG_MAX_LENGTH} caractères, sans virgule.`,
      ];
    } else if (!tags.some((t) => t.toLowerCase() === tag.toLowerCase())) tags.push(tag);
  }
  if (tags.length > TAG_MAX_COUNT && !errors['tags']) {
    errors['tags'] = [`Au plus ${TAG_MAX_COUNT} étiquettes.`];
  }
  return Object.keys(errors).length > 0
    ? { errors }
    : { value: { title, symptom, cause, solution, tags }, errors };
}

/** Returns null when the path is not an article or theme route. */
export function handleKbRoutes(ctx: MockContext): MockResult | null {
  const { method, path, me } = ctx;

  if (path === '/theme') return themeRoute(ctx);

  if (path !== '/articles' && !path.startsWith('/articles/')) return null;
  if (me.role === 'SuperAdmin') return problem(403, 'Forbidden');
  const agent = isAgentRole(me.role);

  if (path === '/articles') {
    if (method === 'GET') return listArticles(ctx);
    if (method !== 'POST') return null;
    if (!agent) return problem(403, 'Forbidden');
    const parsed = parseRequest(ctx.body);
    if (!parsed.value) return problem(422, 'Validation failed', parsed.errors);
    const created: Article = {
      id: `70000000-0000-4000-8000-${String(state.nextId++).padStart(12, '0')}`,
      ...parsed.value,
      status: 'Draft',
      sourceTicketId: null,
      author: { id: me.id, displayName: me.displayName },
      createdAt: new Date().toISOString(),
      publishedAt: null,
    };
    state.articles.unshift(created);
    return { status: 201, body: { ...created } };
  }

  const match = /^\/articles\/([^/]+)(?:\/(publish|unpublish))?$/.exec(path);
  if (!match) return null;
  const found = state.articles.find((a) => a.id === match[1]);
  // A draft is 404 for a Requester, like an article of another tenant.
  if (!found || !visibleTo(found, me)) return problem(404, 'Not found');

  const action = match[2];
  if (!action && method === 'GET') return { status: 200, body: { ...found } };
  if (!agent && (method === 'PUT' || method === 'POST')) return problem(403, 'Forbidden');

  if (!action && method === 'PUT') {
    const parsed = parseRequest(ctx.body);
    if (!parsed.value) return problem(422, 'Validation failed', parsed.errors);
    Object.assign(found, parsed.value);
    return { status: 200, body: { ...found } };
  }
  if (action === 'publish' && method === 'POST') {
    if (found.status === 'Published') return { status: 200, body: { ...found } };
    const errors: Record<string, string[]> = {};
    if (!found.title.trim()) errors['title'] = ['Le titre est obligatoire pour publier.'];
    if (!found.symptom.trim()) errors['symptom'] = ['Le symptôme est obligatoire pour publier.'];
    if (!found.solution.trim()) errors['solution'] = ['La solution est obligatoire pour publier.'];
    if (Object.keys(errors).length > 0) return problem(422, 'Validation failed', errors);
    found.status = 'Published';
    found.publishedAt = new Date().toISOString();
    return { status: 200, body: { ...found } };
  }
  if (action === 'unpublish' && method === 'POST') {
    found.status = 'Draft';
    found.publishedAt = null;
    return { status: 200, body: { ...found } };
  }
  return null;
}

function themeRoute(ctx: MockContext): MockResult | null {
  const { method, me } = ctx;
  if (method === 'GET')
    return { status: 200, body: me.role === 'SuperAdmin' ? paletteDefaults() : currentMockTheme() };
  if (method !== 'PUT' && method !== 'DELETE') return null;
  if (me.role !== 'TenantAdmin') return problem(403, 'Forbidden');

  if (method === 'DELETE') {
    state.theme = null;
    return { status: 200, body: paletteDefaults() };
  }
  const req = (ctx.body ?? {}) as Partial<Theme>;
  const errors: Record<string, string[]> = {};
  for (const field of ['colorPrimary', 'colorSurface', 'colorAccent'] as const) {
    if (typeof req[field] !== 'string' || !isHexColor(req[field])) {
      errors[field] = ['La couleur doit être au format #RRGGBB.'];
    }
  }
  if (Object.keys(errors).length > 0) return problem(422, 'Validation failed', errors);
  const theme: Theme = {
    colorPrimary: req.colorPrimary!.toUpperCase(),
    colorSurface: req.colorSurface!.toUpperCase(),
    colorAccent: req.colorAccent!.toUpperCase(),
  };
  state.theme = theme;
  const result: ThemeUpdateResult = {
    theme: { ...theme },
    warnings: accentContrastWarning(theme.colorAccent, theme.colorSurface)
      ? [ACCENT_CONTRAST_WARNING]
      : [],
  };
  return { status: 200, body: result };
}
