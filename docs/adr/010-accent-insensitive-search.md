# ADR 010 — Recherche d'articles insensible aux accents

Statut : accepté · Date : 2026-11-25

## Contexte
La recherche d'articles (`GET /articles?q=`) utilisait `ILIKE`, qui ignore la casse mais pas les accents : « imprimante » ne trouvait pas « imprimanté ». Pour une base de connaissances en français, où l'on tape souvent sans accents, c'est un défaut. Les caractères `%`, `_` et `\` doivent rester littéraux, et l'ordre des résultats ne change pas (SPEC section 3 : pas de plein texte avancé).

## Décision
Extension PostgreSQL `unaccent`, créée par une migration (`CREATE EXTENSION IF NOT EXISTS unaccent`). Les deux côtés de la comparaison sont désaccentués : `unaccent(colonne) ILIKE unaccent(motif) ESCAPE '\'`. La fonction est exposée à LINQ par `DeskDbFunctions.Unaccent` (fonction native, sans schéma). `unaccent` ne modifie ni `%`, `_` ni `\` : l'échappement du motif reste valable.

Alternative écartée : une fonction SQL immuable avec `translate()` créée par migration. Elle ne couvre qu'une liste de lettres écrite à la main et ajoute du SQL à maintenir ; elle ne se justifie que si l'extension est indisponible.

Vérifié : l'image `postgres:17-alpine` de `docker-compose.yml` et celle des Testcontainers (la même) embarquent les extensions « contrib », dont `unaccent`, qui est marquée « trusted » (créable par le propriétaire de la base, sans superutilisateur).

## Conséquences
- « imprimante » trouve « imprimanté » et inversement, dans le titre, le symptôme, la cause, la solution et les étiquettes.
- `unaccent()` est `STABLE`, pas `IMMUTABLE` : aucun index fonctionnel n'est possible dessus. Sans importance ici (recherche `ILIKE '%mot%'` déjà sans index, volume d'un helpdesk d'entreprise, filtrage préalable par `TenantId`).
- Un hébergeur PostgreSQL qui interdit `CREATE EXTENSION` doit activer `unaccent` avant la migration ; les services gérés courants l'autorisent.
- `Down` supprime l'extension.
