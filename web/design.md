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
| `--on-darkest`      | Noir             | Secours des `--on-*` quand ni foncé ni blanc n'atteignent 4,5:1        |                              |

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
- Renouvellement du jeton : le service ne renouvelle qu'à la demande, quand le JWT en mémoire expire dans moins de 60 s ou après un 401 du hub, jamais à chaque tentative (chaque renouvellement fait tourner le cookie de refresh, et rejouer un ancien cookie révoque la famille). Il partage le single-flight d'`AuthService.refreshToken()` avec l'intercepteur. Un 401 du renouvellement arrête les essais, efface la session et ramène vers `/login`.
- Limite connue, multi-onglets : le single-flight ne vaut que par onglet. Deux onglets dont les jetons expirent en même temps peuvent renouveler en parallèle avec le même cookie ; la rotation côté serveur peut alors en pénaliser un (retour à la connexion). Une coordination inter-onglets (`BroadcastChannel` ou verrou Web Locks) reste à faire si le cas se présente.
- Faux hub (`npm run start:mock`, `core/realtime/hub-factory.mock.ts`) : silencieux par défaut. Console : `deskflowMockRealtime.start(10)`, `.emit('ticketReplied')`, `.stop()` ; ou `sessionStorage.setItem('deskflow.mock.realtime', '15'); location.reload();`.

## Base de connaissances, thème et administration (phase 4)

- Routes lazy `/kb`, `/kb/:id`, `/kb/new`, `/kb/:id/edit` (`features/kb`). Lecture pour Demandeur, Agent et Administrateur ; création, édition, publication et dépublication pour Agent et Administrateur (garde `roleGuard`, ergonomique : l'API refuse de toute façon). Le SuperAdmin n'a pas d'entrée « Base de connaissances ». Un brouillon est un 404 pour un demandeur : l'écran 404 s'affiche.
- Liste : recherche `q` avec anti-rebond de 300 ms (Entrée recherche tout de suite), pagination de 10, filtre de statut et badge « Brouillon » réservés aux agents, annonce du nombre de résultats dans une région `aria-live="polite"`.
- Édition : formulaire réactif typé ; titre de 3 à 200 caractères ; étiquettes séparées par des virgules (10 au plus, 30 caractères chacune, doublons retirés). « Enregistrer et publier » vérifie symptôme et solution avant tout appel ; si la publication est refusée, le brouillon reste enregistré et le formulaire le dit. La dépublication demande une confirmation en deux clics.
- Suggestions (`features/tickets/article-suggestions.ts`) sous le titre de `/tickets/new` : 400 ms sans frappe, 3 caractères au moins, `GET /articles?q=…&pageSize=5`, `switchMap` (requête obsolète annulée), échec silencieux, liens `target="_blank"` avec mention « s'ouvre dans un nouvel onglet ».
- `/admin` : onglets Utilisateurs, Invitations, Catégories, SLA, Thème (rôle `tablist`, flèches, Début, Fin). Catégories : 409 à la suppression = « utilisée par des tickets » ; 409 à la création ou au renommage = nom déjà pris. SLA : quatre lignes en minutes, aide lisible (`= 4 h`), résolution >= première réponse, rappel « ne s'applique qu'aux nouveaux tickets ».
- Thème : trois `<input type="color">` natifs et trois champs hexadécimaux synchronisés. L'aperçu écrit les cinq variables sur son propre conteneur (`.theme-scope`, `ThemeService.variablesFor`), jamais sur `:root` : seul l'enregistrement (ou la réinitialisation) appelle `ThemeService.apply`. `.theme-scope` (dans `styles.css`) redéclare les teintes dérivées (`--tint-*`), car une variable définie avec `var()` sur `:root` est résolue à cet endroit et ne suivrait pas le conteneur. Le bouton d'accent de l'aperçu est une illustration (`aria-hidden`, pas un `button`) : le seul bouton d'accent du formulaire est « Enregistrer le thème ».
- Couleur du texte `--on-*` : on essaie dans l'ordre `--on-dark`, `--on-light`, puis `--on-darkest` (noir, token de secours de `tokens.css`) et on garde le premier qui atteint 4,5:1 ; sinon le meilleur des trois (`chooseOnColor`, `core/theme/contrast.ts`). Les défauts ne changent pas (texte foncé sur le bleu ciel, blanc sur le rouge ambré). Sans le secours, les teintes moyennes (luminance d'environ 0,18 à 0,27, pire cas `#828282` à 3,87:1) n'atteignaient pas 4,5:1 avec le texte foncé `#0B2A3C`. L'aperçu et `:root` utilisent la même règle (`ThemeService.variablesFor`).
- Avertissements non bloquants calculés côté client, fusionnés sans doublon avec ceux de l'API : accent/surface sous 3:1 (comme l'API), texte principal (encre) sous 4,5:1 sur la surface, et texte `--on-*` sous 4,5:1 (simple filet de sécurité depuis `--on-darkest`).
- Onglets de `/admin` : un panneau est créé à la première visite puis seulement masqué, pour ne pas perdre une saisie non enregistrée (thème, SLA). Catégories : le focus clavier suit chaque étape (Confirmer, champ de renommage, bouton d'origine, puis titre de la section).
- Backend simulé : `core/mock/mock-kb.ts` (articles de démonstration publiés et brouillons, dont un avec `sourceTicketId`, recherche sans accents ni casse, publication, thème) et `mock-tickets.ts` (catégories, SLA). Mêmes rôles et mêmes 404/422/409 que l'API ; les SLA modifiés valent pour les tickets créés ensuite.
