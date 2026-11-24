# Contrat de la phase 4 : base de connaissances, thème, SLA

Complète `docs/api/openapi.json` (version `0.3.0-phase4`, gelée par l'orchestrateur). SPEC sections 6, 7 (US-17 à US-20), 9 et 10. Tout changement passe par l'orchestrateur.

## 1. Articles (US-17, US-18)

| Route | Rôle | Règles |
| --- | --- | --- |
| `GET /articles` | Connecté | Requester : articles `Published` seulement (le paramètre `status` est ignoré). Agent et TenantAdmin : tous, filtrables par `status`. Tri : voir `q` ci-dessous ; sans `q`, `Published` par `PublishedAt` décroissant puis les brouillons par `CreatedAt` décroissant. |
| `GET /articles/{id}` | Connecté | Un brouillon est 404 pour un Requester ; un article d'une autre entreprise est 404 pour tous. |
| `POST /articles` | Agent, TenantAdmin | Crée un `Draft`, `AuthorId` = l'utilisateur, `SourceTicketId` nul. |
| `PUT /articles/{id}` | Agent, TenantAdmin | Remplace titre, symptôme, cause, solution, étiquettes ; le statut ne change pas ; dernier enregistrement gagnant (pas de `rowVersion`). |
| `POST /articles/{id}/publish` | Agent, TenantAdmin | `Draft` vers `Published`, `PublishedAt` = maintenant ; déjà publié : 200 sans changement ; 422 si titre, symptôme ou solution est vide (une cause vide est permise). |
| `POST /articles/{id}/unpublish` | Agent, TenantAdmin | Retour à `Draft`, `PublishedAt` effacé ; déjà `Draft` : 200 sans changement. |

Pas de suppression d'article dans le MVP. Un SuperAdmin (sans entreprise) reçoit 403 sur toutes les routes `/articles`.

**Recherche** (`q`, 100 caractères au plus) : mots séparés par des espaces ; les mots de moins de 2 caractères sont ignorés ; tous les mots restants doivent apparaître, sans tenir compte de la casse (et si possible des accents), dans le titre, le symptôme, la cause, la solution ou les étiquettes. Les articles dont le titre contient tous les mots passent avant les autres, puis `PublishedAt` décroissant. Recherche simple (`ILIKE`), pas de plein texte avancé (SPEC section 3). Les caractères `%`, `_` et `\` de `q` sont traités comme du texte. Un `q` vide ou sans mot utile renvoie la liste sans filtre.

**Suggestions à la saisie** : aucune route dédiée. L'écran `/tickets/new` appelle `GET /articles?q=<titre en cours>&pageSize=5` (Requester : donc publiés seulement), après 400 ms sans frappe et pour au moins 3 caractères saisis.

**Étiquettes** : au plus 10, 30 caractères chacune, sans virgule (422 sinon), supprimées en doublon ; l'API les expose sous forme de tableau et les stocke séparées par des virgules (`KnowledgeArticle.Tags`).

**`excerpt`** (résumé) : début du symptôme, 200 caractères au plus. **`author`** : `UserRef` de l'auteur (un utilisateur désactivé reste affiché).

Les brouillons créés par `KbDraftConsumer` (phase 3) apparaissent dans la liste des agents avec `sourceTicketId` renseigné. L'utilisateur auteur de ce brouillon peut ne plus être actif : l'API ne l'exige pas.

## 2. Thème (US-19)

- `GET /theme` : les trois couleurs de l'entreprise de l'utilisateur ; `GET /me` continue de renvoyer le même objet dans `tenant.theme`.
- `PUT /theme` (TenantAdmin) : `{ colorPrimary, colorSurface, colorAccent }`, chacune `#RRGGBB` (majuscules ou minuscules ; l'API enregistre en **majuscules**), 422 sinon avec `errors` par champ. Réponse : `{ theme, warnings }`. Avertissement non bloquant, en français, si le contraste entre `colorAccent` et `colorSurface` est inférieur à 3:1 (WCAG). La couleur du texte (`on-*`) n'est pas stockée : le front la calcule (noir ou blanc selon le meilleur contraste, au moins 4,5:1 toujours atteignable).
- `DELETE /theme` (TenantAdmin) : remet la palette par défaut (`#7CC4EE`, `#F4F7FA`, `#D63A14`) et la renvoie.
- Un SuperAdmin n'a pas d'entreprise : `GET /theme` renvoie les couleurs par défaut, `PUT` et `DELETE` répondent 403.

## 3. SLA (US-20)

- `GET /sla-policies` (TenantAdmin) : exactement quatre lignes, ordre `Low`, `Normal`, `High`, `Urgent`.
- `PUT /sla-policies` (TenantAdmin) : `{ policies: [4 lignes] }`, une ligne par priorité ; `firstResponseMinutes` de 1 à 43 200 ; `resolutionMinutes` de 1 à 43 200 et **supérieur ou égal** à `firstResponseMinutes` ; 422 sinon, avec `errors`.
- Les nouveaux SLA ne valent que pour les tickets **créés ensuite** (et pour le recalcul déjà prévu quand la priorité d'un ticket change : `Ticket.ChangePriority` lit la table courante). Les échéances déjà posées ne sont pas recalculées.

## 4. Catégories (US-20)

Aucun changement de contrat : `GET/POST/PUT/DELETE /categories` existent depuis la phase 2. Le frontend ajoute l'écran de gestion.

## 5. Écrans (frontend)

- `/kb` : liste avec recherche (`q`) et pagination ; les agents voient aussi les brouillons (filtre par statut) ; `/kb/:id` : lecture ; `/kb/:id/edit` et `/kb/new` : édition (Agent, TenantAdmin) avec publier et dépublier.
- `/tickets/new` : suggestions d'articles sous le titre (voir plus haut).
- `/admin` : onglets Catégories, SLA, Thème (en plus d'utilisateurs et invitations existants). Thème : trois sélecteurs de couleur, consigne « Choisissez deux couleurs discrètes et une couleur qui claque. », aperçu en direct (mini liste de tickets et un bouton), bouton « Réinitialiser », avertissements affichés ; après enregistrement, le thème s'applique immédiatement (variables CSS sur `:root`).
- Navigation : entrée « Base de connaissances » pour tous les rôles connectés avec une entreprise.
