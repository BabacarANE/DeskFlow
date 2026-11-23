# Design de l'interface DeskFlow

Ce document décrit les tokens existants de `src/styles/tokens.css` et les règles d'usage (SPEC section 10).
Il remplace le skill de design, non installé dans ce projet : il ne crée aucun nouveau token.

## Règle absolue

- La palette est verrouillée. Les valeurs hexadécimales n'existent que dans `src/styles/tokens.css`.
- Aucun composant, gabarit ou fichier CSS ne contient de couleur en dur. Le test `src/app/no-hardcoded-hex.spec.ts` le vérifie.
- Le thème d'une entreprise remplace les variables sur `:root` à l'exécution (`ThemeService`), sans recompilation.

## Tokens de couleur

| Token               | Défaut           | Rôle                                                                   | Classes Tailwind             |
| ------------------- | ---------------- | ---------------------------------------------------------------------- | ---------------------------- |
| `--color-primary`   | Bleu ciel        | Discrète : barre de navigation, éléments sélectionnés, fonds de badges | `bg-primary`, `text-primary` |
| `--color-surface`   | Blanc nuage      | Discrète : fond de l'application                                       | `bg-surface`                 |
| `--color-accent`    | Rouge ambré      | Qui claque : le bouton d'action principal, alertes SLA                 | `bg-accent`                  |
| `--color-ink`       | Texte principal  | Texte courant, contour du focus                                        | `text-ink`                   |
| `--color-ink-muted` | Texte secondaire | Aides, métadonnées                                                     | `text-ink-muted`             |
| `--on-primary`      | Foncé ou blanc   | Texte sur `--color-primary`, calculé selon le contraste WCAG           | `text-(--on-primary)`        |
| `--on-accent`       | Foncé ou blanc   | Texte sur `--color-accent`, calculé selon le contraste WCAG            | `text-(--on-accent)`         |
| `--on-dark`         | Foncé            | Valeur « foncé » vers laquelle pointent les `--on-*`                   |                              |
| `--on-light`        | Blanc            | Valeur « blanc » vers laquelle pointent les `--on-*`                   |                              |

Teintes dérivées, calculées avec `color-mix()` : elles suivent le thème sans hexadécimal. Elles sont définies sur `:root` (`--tint-*`) puis exposées à Tailwind par `@theme static inline` ; les déclarer directement dans `@theme` ferait inliner la palette par défaut au lieu de suivre le thème.

| Token                  | Dérivation                  | Usage                                        | Classes           |
| ---------------------- | --------------------------- | -------------------------------------------- | ----------------- |
| `--color-primary-soft` | primaire à 22 % sur blanc   | Survol, bannières, badges                    | `bg-primary-soft` |
| `--color-card`         | surface à 35 % sur blanc    | Cartes, zones de saisie, boutons secondaires | `bg-card`         |
| `--color-line`         | encre à 18 % sur la surface | Bordures et séparateurs                      | `border-line`     |

## Thème par entreprise

- `GET /me` renvoie `tenant.theme` (`colorPrimary`, `colorSurface`, `colorAccent`).
- `ThemeService.apply()` écrit `--color-primary`, `--color-surface`, `--color-accent`, `--on-primary` et `--on-accent` sur `:root`.
- `--on-*` pointe vers `--on-light` (blanc) ou `--on-dark` selon le meilleur contraste WCAG (`core/theme/contrast.ts`).
- Un thème absent ou mal formé (`#RRGGBB` exigé) rétablit la palette par défaut ; la déconnexion fait de même.
- Le texte principal et secondaire (`--color-ink*`) ne sont pas thémés : une surface très sombre est hors périmètre (le mode sombre est hors MVP).

## Règles d'usage

- Environ 60 % de surface, 30 % de primaire, 10 % d'accent. L'accent ne couvre jamais une grande surface.
- Un seul bouton d'accent (`.btn-accent`) par écran : l'action principale. Les autres boutons sont `.btn-secondary`.
- Contraste minimal de 4,5:1 pour tout texte. Le rouge ambré sur le blanc nuage n'atteint pas ce seuil (environ 4,4:1) : ne jamais écrire de texte en `--color-accent` sur la surface. L'accent sert de fond, avec `--on-accent`.
- Les messages d'erreur et de succès utilisent `bg-primary-soft`, une bordure `border-line` et le texte principal, avec `role="alert"` ou `role="status"`.
- Focus visible : contour de 3 px couleur `--color-ink` sur `:focus-visible` (défini dans `styles.css`), jamais supprimé.
- Cibles tactiles d'au moins 44 px (`min-h-11`), 36 px pour les petits boutons de tableau (`.btn-small`).

## Composants utilitaires (`src/styles.css`, couche `components`)

`.card`, `.btn`, `.btn-accent`, `.btn-secondary`, `.btn-small`, `.field`, `.badge`, `.badge-muted`, `.badge-primary`, `.badge-outline`, `.badge-accent`, `.table-wrap`, `.table`.
Ils n'utilisent que les tokens ci-dessus.

## Tickets (phase 2)

- Badges de statut et de priorité : le texte dit toujours la valeur, la couleur ne fait que la renforcer. Nouveau : `.badge-primary` ; Ouvert et Normale : `.badge` ; En attente, Fermé et Basse : `.badge-muted` ; Résolu et Haute : `.badge-outline`.
- L'accent est réservé à Urgente (`.badge-accent`) et à l'alerte « Échéance SLA dépassée » (même classe, texte `--on-accent`), en plus du bouton principal de l'écran (« Nouveau ticket », « Créer le ticket », « Envoyer »).
- Une note interne est visuellement distincte : bordure en tirets `border-ink`, fond `bg-primary-soft` et mention « Note interne, invisible pour le demandeur » (jamais la couleur seule). Les notes ne sont envoyées par l'API qu'aux agents.
- Le changement de statut propose uniquement les transitions valides (`core/api/ticket-rules.ts`, miroir du contrat).
- Échéance SLA dépassée (`isSlaBreached`) : ticket `New` ou `Open` dont l'échéance de résolution est passée (ou de première réponse pour `New`). Le SLA est suspendu en `Pending`.
- Les filtres de la liste survivent à une visite d'un ticket (`TicketListState`, fourni à la racine).

## Gabarit d'écran

- En-tête `bg-primary` + `text-(--on-primary)`, navigation selon le rôle, nom de l'utilisateur, bouton « Se déconnecter ».
- Bannière « Mode démo, données fictives » (`role="status"`) au-dessus de l'en-tête quand `/me` renvoie `isDemo`.
- Lien d'évitement « Aller au contenu », contenu dans `main#contenu` (largeur maximale 64 rem).
- Chaque liste a trois états : chargement (`role="status"`), vide, erreur avec bouton « Réessayer » (`role="alert"`).
- Les actions destructrices (désactiver, révoquer, suspendre) demandent une confirmation en deux clics, sans fenêtre modale.

## Développer sans backend

`npm run start:mock` lance l'application avec un backend simulé en mémoire (`core/mock`).
La démo propose les trois profils ; pour essayer l'écran SuperAdmin, exécuter dans la console du navigateur :
`sessionStorage.setItem('deskflow.mock.role', 'superadmin'); location.reload();`.
Le backend simulé suit le contrat figé `docs/api/openapi.json` pour les tickets (`core/mock/mock-tickets.ts`) : transitions valides, `rowVersion` et 409, notes internes masquées au demandeur, filtres, tri et pagination. Une trentaine de tickets de démonstration permettent de voir la pagination et les échéances dépassées.
`npm start` utilise le proxy `proxy.conf.json` vers l'API sur `http://localhost:5148`.

## Temps réel (phase 3)

- Contrat : `docs/api/events.md` section 5 (hub `/hubs/notifications`, événements `ticketCreated`, `ticketAssigned`, `ticketReplied`). Seuls Agent et TenantAdmin se connectent ; SuperAdmin et Requester n'ouvrent aucune connexion.
- `RealtimeService` : jeton lu en mémoire à chaque tentative (jamais stocké), reprise avec backoff (1 s à 30 s, jitter), jeton renouvelé avant chaque nouvel essai, arrêt à la déconnexion. Hors ligne = état discret, l'application reste utilisable.
- Cloche (`shared/notification-bell.ts`) : bouton secondaire (jamais d'accent), compteur en texte dans `aria-label` et dans le badge, panneau CDK overlay (`role="dialog"`), flèches, Début, Fin, Échap, Tab en bord de liste ferme et rend le focus. Annonce `aria-live="polite"` à chaque arrivée. Liste en mémoire limitée à 50, sans persistance.
- Un événement rafraîchit sans état de chargement la liste (anti-rebond 400 ms, filtres et saisie conservés) et le ticket affiché s'il est concerné (le `rowVersion` est mis à jour ; si une action est en cours, le rafraîchissement attend sa fin).
- Faux hub (`npm run start:mock`, `core/realtime/hub-factory.mock.ts`) : silencieux par défaut. Console : `deskflowMockRealtime.start(10)`, `.emit('ticketReplied')`, `.stop()` ; ou `sessionStorage.setItem('deskflow.mock.realtime', '15'); location.reload();`.
