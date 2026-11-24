import { Article, ArticleSummary, Page } from '../core/api/models';

/** Test data builders for the knowledge base screens. */
export function articleSummary(
  id: string,
  overrides: Partial<ArticleSummary> = {},
): ArticleSummary {
  return {
    id,
    title: `Article ${id}`,
    status: 'Published',
    tags: [],
    excerpt: 'Le VPN refuse la connexion.',
    createdAt: '2026-11-01T09:00:00Z',
    publishedAt: '2026-11-02T09:00:00Z',
    ...overrides,
  };
}

export function article(id: string, overrides: Partial<Article> = {}): Article {
  return {
    id,
    title: `Article ${id}`,
    symptom: 'Le VPN refuse la connexion.',
    cause: 'Mot de passe expiré.',
    solution: 'Renouvelez le mot de passe.',
    tags: ['vpn', 'accès'],
    status: 'Published',
    sourceTicketId: null,
    author: { id: 'u1', displayName: 'Adam Agent' },
    createdAt: '2026-11-01T09:00:00Z',
    publishedAt: '2026-11-02T09:00:00Z',
    ...overrides,
  };
}

export function articlePage(items: ArticleSummary[], total = items.length): Page<ArticleSummary> {
  return { items, total, page: 1, pageSize: 10 };
}
