# Cahier des charges MVP — Plateforme de ticketing Helpdesk (.NET, Angular, RabbitMQ)

Nov 23, 2026 · @Babacar ANE

## 1. Contexte et objectif

Ce document est la source de vérité pour construire le MVP de **DeskFlow**, une plateforme de ticketing helpdesk multi-entreprises ; il est écrit pour être exécuté par Claude Code, qui a les droits de commit, push et pull request sur le dépôt.

**Objectif** : livrer le plus vite possible un MVP démontrable et déployé, qui sert de projet phare du portfolio de Babacar (Master 2 Développement & Data, recherche d'alternance fullstack en Île-de-France).

**Ce que le projet doit démontrer**

- **.NET** : API REST propre, EF Core, service de fond (worker) séparé de l'API.
- **Angular** : application moderne (composants standalone, signals, routes lazy), thème dynamique par entreprise.
- **RabbitMQ** : traitement asynchrone fiable (retries, dead-letter queue, outbox, idempotence).
- **Authentification** : OAuth Google et GitHub, rôles, isolation stricte des données entre entreprises.
- **Pratiques professionnelles** : tests d'intégration, Docker Compose, CI, documentation, journal des problèmes rencontrés et résolus.

**Principes de travail**

- Vitesse avant perfection : le plus simple qui marche, puis on améliore.
- Quand ce document ne tranche pas, Claude Code choisit l'option la plus simple et note la décision dans un ADR (section 12).
- Nom de travail : DeskFlow (modifiable). Code, identifiants et messages de commit en anglais ; interface et documentation en français.

## 2. Périmètre du MVP

Le MVP est atteint à la fin de la phase 3 (section 13) : un utilisateur se connecte avec Google ou GitHub, crée un ticket, un agent le traite, et les e-mails, relances SLA et brouillons d'articles partent via RabbitMQ.

**Inclus**

- Multi-entreprise (base partagée, colonne `TenantId`) avec 4 rôles.
- Connexion Google et GitHub, invitations par e-mail, comptes de démo en un clic.
- Tickets : création, liste filtrable, détail, commentaires publics et internes, statuts, priorités, catégories, assignation.
- SLA simple par priorité, avec escalade automatique.
- E-mails transactionnels asynchrones : accusé de réception, assignation, nouvelle réponse, escalade.
- Base de connaissances : articles Symptôme / Cause / Solution, brouillon proposé quand un ticket est résolu.
- Thème par entreprise (3 couleurs) avec une palette par défaut.
- Notifications temps réel (SignalR) pour les agents.
- Données de démonstration fictives, clairement signalées comme telles.
- Docker Compose, CI, tests, documentation et une démo déployée.

**Exclus du MVP**

- Pièces jointes, chat en direct, application mobile.
- Facturation, paiement, abonnements.
- Mots de passe locaux, SAML, double authentification.
- Recherche plein texte avancée (une recherche simple suffit).
- Multi-langue (français uniquement) et IA.
- Tableaux de bord et rapports avancés, pour ne pas refaire le projet dashboard analytics existant.
- Un utilisateur rattaché à plusieurs entreprises (une seule dans le MVP).

## 3. Stack et architecture

Le MVP est un monolithe modulaire (une API et un worker .NET) plutôt que des microservices : on livre vite tout en montrant un vrai flux asynchrone.

| Couche | Choix | Remarque |
| --- | --- | --- |
| Backend | ASP.NET Core sur .NET 10 (LTS) | .NET 8 sort du support le 10 novembre 2026 ; .NET 10 est supporté jusqu'au 14 novembre 2028 |
| Données | EF Core + PostgreSQL | migrations versionnées, filtres globaux pour le multi-entreprise |
| Messagerie | RabbitMQ avec le client officiel `RabbitMQ.Client` | pas de MassTransit v9, voir ci-dessous |
| Temps réel | SignalR | hébergé dans l'API |
| E-mail | MailKit (SMTP) | Mailpit en développement, SMTP configurable en production |
| Frontend | Angular (dernière version stable), TypeScript strict | composants standalone, signals, routes lazy, Tailwind CSS |
| Authentification | OAuth2 Google + GitHub, JWT court + refresh token | section 5 |
| Conteneurs | Docker Compose | postgres, rabbitmq, mailpit, api, worker, web |
| CI/CD | GitHub Actions | section 11 |
| Logs | Serilog en JSON | identifiant de corrélation propagé dans les messages |

**Choix de la messagerie.** MassTransit v9 est un produit commercial sous licence ; seule la v8 reste en Apache 2.0, avec des correctifs de sécurité annoncés [jusqu'à fin 2026](https://masstransit.io/introduction/v9-announcement). Pour un nouveau projet, on utilise donc le client officiel `RabbitMQ.Client` (API asynchrone) avec une petite couche maison : déclaration de la topologie, publication avec confirmations, consommateurs avec ack manuel. C'est aussi plus démonstratif : exchanges, bindings, acks, DLX et prefetch sont visibles dans le code. Versions .NET : [calendrier de support Microsoft](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/).

**Structure du dépôt (monorepo)**

```
deskflow/
  src/
    Desk.Core/            domaine, enums, contrats d'événements
    Desk.Infrastructure/  EF Core, outbox, couche RabbitMQ, e-mail
    Desk.Api/             Web API, auth, SignalR
    Desk.Worker/          consommateurs, détection SLA, dispatcher outbox
  tests/
    Desk.UnitTests/
    Desk.IntegrationTests/
  web/                    application Angular
  docs/                   architecture, ADR, troubleshooting, setup OAuth
  docker-compose.yml
  .github/workflows/
```

**Flux principal**

1. L'API enregistre le ticket et un message dans la table `OutboxMessage` dans la même transaction.
2. Le dispatcher du Worker lit l'outbox et publie sur l'exchange topic `desk.events`, avec confirmations de publication.
3. Chaque consommateur a sa propre file liée à l'exchange et traite le message de façon idempotente.
4. En cas d'échec : nouvelles tentatives avec délai, puis dead-letter queue (section 8).
5. Les notifications temps réel passent par un consommateur hébergé dans l'API, car le hub SignalR y vit.

## 4. Multi-entreprise et rôles

Une seule base PostgreSQL est partagée ; chaque ligne métier porte un `TenantId` et l'isolation est appliquée automatiquement, jamais à la main requête par requête.

**Mécanisme d'isolation**

- `ITenantContext` fournit le `TenantId` lu dans le claim `tid` du JWT.
- Un filtre global EF Core (`HasQueryFilter`) restreint toutes les entités portées par une entreprise.
- Un intercepteur `SaveChanges` renseigne `TenantId` à la création et refuse toute écriture vers une autre entreprise.
- Le SuperAdmin n'a pas d'entreprise ; ses endpoints désactivent le filtre explicitement (`IgnoreQueryFilters`) et se limitent à la gestion des entreprises.
- Chaque message RabbitMQ contient le `TenantId` ; le consommateur le positionne avant tout accès aux données.
- Le numéro de ticket est séquentiel par entreprise, unique sur `(TenantId, Number)`.

| Rôle | Portée | Droits |
| --- | --- | --- |
| SuperAdmin | Plateforme | Créer et suspendre une entreprise, inviter son premier admin. Aucun accès au contenu des tickets. |
| TenantAdmin | Entreprise | Tout ce que fait un agent, plus : utilisateurs, invitations, catégories, SLA, thème, publication d'articles. |
| Agent | Entreprise | Voir tous les tickets de l'entreprise, répondre (public ou interne), changer statut, priorité et assignation, rédiger et publier des articles. |
| Requester | Entreprise | Créer des tickets, voir et commenter uniquement les siens, lire les articles publiés. |

**Onboarding d'une entreprise**

1. Le SuperAdmin crée l'entreprise (nom, slug) et invite son premier TenantAdmin par e-mail.
2. L'invité ouvre le lien et se connecte avec Google ou GitHub ; l'e-mail vérifié du fournisseur doit correspondre à l'e-mail invité.
3. Le TenantAdmin invite ensuite ses agents et ses demandeurs.
4. Sans invitation, aucun compte ne se crée (sauf les comptes de démo, section 5).

**Test obligatoire** : des tests d'intégration prouvent qu'un utilisateur de l'entreprise A ne peut ni lire, ni lister, ni modifier de données de l'entreprise B (tickets, commentaires, articles, utilisateurs, catégories).

## 5. Authentification

L'API gère OAuth côté serveur et émet ses propres jetons ; Angular ne manipule jamais de secret du fournisseur.

**Flux de connexion**

1. Angular redirige vers `GET /api/v1/auth/external/{provider}` (`google` ou `github`), avec `?invitation=<token>` si l'utilisateur a une invitation.
2. L'API lance le flux OAuth2 "authorization code" avec le fournisseur et reçoit le callback.
3. L'API retrouve ou crée l'utilisateur (invitation obligatoire à la première connexion) et lie un `ExternalLogin`.
4. L'API pose un refresh token dans un cookie `HttpOnly`, `Secure`, `SameSite=Lax`, puis redirige vers `/auth/callback` du front.
5. Le front appelle `POST /api/v1/auth/refresh` ; l'API renvoie un access token JWT de 15 minutes, gardé en mémoire (jamais dans `localStorage`).
6. Le refresh token (7 jours) est stocké haché, renouvelé à chaque usage ; la réutilisation d'un ancien token révoque toute sa famille.

**Claims du JWT** : `sub`, `tid`, `role`, `email`, `name`.

**Fournisseurs**

- Google : `Microsoft.AspNetCore.Authentication.Google`, e-mail accepté seulement si `email_verified` est vrai.
- GitHub : `AspNet.Security.OAuth.GitHub`, scope `user:email`, e-mail principal vérifié uniquement.
- Microsoft Entra : hors MVP.
- URI de redirection à déclarer chez chaque fournisseur : `{API_BASE}/signin-google` et `{API_BASE}/signin-github`.

**Contrainte de déploiement** : le front et l'API doivent partager le même site (même domaine, ou reverse proxy nginx devant les deux) pour que le cookie `SameSite=Lax` fonctionne. Docker Compose place nginx devant ; en développement, `ng serve` utilise un proxy vers l'API.

**Compte de démo** : `POST /api/v1/auth/demo` avec un rôle (admin, agent, demandeur) connecte un utilisateur fictif de l'entreprise de démo. L'endpoint n'existe que si `Demo:Enabled=true`, et une bannière "Mode démo, données fictives" reste visible. Cela permet à un recruteur d'essayer l'application sans compte.

**Sécurité**

- Autorisation par politiques ASP.NET Core côté API ; les guards Angular ne servent qu'à l'ergonomie.
- CORS restreint, rate limiting sur `/auth`, secrets en variables d'environnement ou `user-secrets`, jamais commités.
- Action manuelle de Babacar : créer l'application Google (console Google Cloud) et l'OAuth App GitHub, puis renseigner les secrets. Claude Code documente les étapes dans `docs/setup-oauth.md` et ne peut pas créer ces identifiants.

## 6. Modèle de données

Quinze entités suffisent (les treize initiales, plus `KnowledgeArticle` et `SlaBreach`, marqueur d'émission unique de l'alerte SLA, ADR 009) pour le MVP ; les données métier portent un `TenantId` (nul pour le SuperAdmin dans `User`). `Tenant`, `ExternalLogin`, `RefreshToken`, `OutboxMessage` et `ProcessedMessage` n'en portent pas (`SlaBreach` en porte un, filtré comme les autres) : l'outbox transporte le `TenantId` dans le message publié.

| Entité | Champs clés | Règles |
| --- | --- | --- |
| Tenant | Id, Name, Slug, ColorPrimary, ColorSurface, ColorAccent, IsActive, CreatedAt | Slug unique ; couleurs au format `#RRGGBB`, défauts en section 10 |
| User | Id, TenantId (nul pour le SuperAdmin), Email, DisplayName, AvatarUrl, Role, IsActive, CreatedAt | E-mail unique globalement |
| ExternalLogin | UserId, Provider, ProviderKey | Unique sur (Provider, ProviderKey) |
| RefreshToken | Id, UserId, TokenHash, FamilyId, ExpiresAt, RevokedAt, ReplacedById | Rotation et détection de réutilisation |
| Invitation | Id, TenantId, Email, Role, TokenHash, ExpiresAt, AcceptedAt | Valide 7 jours, usage unique |
| Category | Id, TenantId, Name | Nom unique par entreprise |
| Ticket | Id, TenantId, Number, Title, Description, Status, Priority, CategoryId, RequesterId, AssigneeId, CreatedAt, UpdatedAt, FirstResponseAt, ResolvedAt, ResponseDueAt, ResolutionDueAt, EscalatedAt, RowVersion | Numéro séquentiel par entreprise ; concurrence optimiste via `RowVersion` |
| TicketComment | Id, TenantId, TicketId, AuthorId, Body, IsInternal, CreatedAt | Les commentaires internes sont invisibles pour un Requester |
| TicketEvent | Id, TenantId, TicketId, ActorId, Type, OldValue, NewValue, At | Historique d'audit : statut, assignation, priorité |
| SlaPolicy | Id, TenantId, Priority, FirstResponseMinutes, ResolutionMinutes | Créée avec l'entreprise, modifiable par le TenantAdmin |
| KnowledgeArticle | Id, TenantId, Title, Symptom, Cause, Solution, Tags, Status, SourceTicketId, AuthorId, CreatedAt, PublishedAt | Les brouillons ne sont visibles que des agents |
| OutboxMessage | Id, Type, Payload (JSON), CreatedAt, DispatchedAt | Écrit dans la même transaction que la donnée métier |
| ProcessedMessage | MessageId, Consumer, ProcessedAt | Clé (MessageId, Consumer) pour l'idempotence |

**Statuts d'un ticket** : `New`, `Open`, `Pending` (en attente du demandeur), `Resolved`, `Closed`. Transitions autorisées : New→Open, Open→Pending, Pending→Open, Open→Resolved, Resolved→Open (réouverture), Resolved→Closed. Toute autre transition est refusée avec une erreur 409.

**Priorités** : `Low`, `Normal`, `High`, `Urgent`.

**SLA par défaut** (modifiables), en minutes :

| Priorité | Première réponse | Résolution |
| --- | --- | --- |
| Urgent | 30 | 240 |
| High | 120 | 480 |
| Normal | 480 | 1440 |
| Low | 1440 | 4320 |

Le SLA court uniquement tant que le ticket est en `New` ou `Open` ; il est suspendu en `Pending`.

## 7. Fonctionnalités MVP (user stories)

Vingt et une user stories, classées par phase de livraison (section 13) ; chacune est terminée quand ses critères de la section 15 s'appliquent et que les tests passent.

| ID | En tant que | Je veux | Phase |
| --- | --- | --- | --- |
| US-01 | SuperAdmin | créer une entreprise et inviter son premier admin | 1 |
| US-02 | Invité | me connecter avec Google ou GitHub grâce à mon invitation | 1 |
| US-03 | Visiteur | essayer l'application avec un compte de démo en un clic | 1 |
| US-04 | TenantAdmin | inviter des agents et des demandeurs, et désactiver un utilisateur | 1 |
| US-05 | Requester | créer un ticket (titre, description, catégorie, priorité) | 2 |
| US-06 | Requester | voir mes tickets, leur statut, et y répondre | 2 |
| US-07 | Agent | lister les tickets de l'entreprise avec filtres (statut, priorité, assigné, catégorie), tri et pagination | 2 |
| US-08 | Agent | m'assigner un ticket ou l'assigner à un collègue | 2 |
| US-09 | Agent | changer le statut et la priorité selon les transitions valides | 2 |
| US-10 | Agent | répondre au demandeur ou écrire une note interne | 2 |
| US-11 | Utilisateur | voir l'historique des événements d'un ticket | 2 |
| US-12 | Requester | recevoir un accusé de réception par e-mail à la création | 3 |
| US-13 | Agent | être notifié (e-mail et temps réel) à l'assignation et quand le demandeur répond | 3 |
| US-14 | Requester | être prévenu par e-mail d'une nouvelle réponse publique | 3 |
| US-15 | TenantAdmin | que les tickets hors SLA soient escaladés automatiquement | 3 |
| US-16 | Agent | recevoir un brouillon d'article pré-rempli quand je résous un ticket | 3 |
| US-17 | Agent | éditer, publier et dépublier un article | 4 |
| US-18 | Requester | rechercher dans les articles publiés, avec des suggestions pendant la saisie du titre d'un ticket | 4 |
| US-19 | TenantAdmin | choisir les 3 couleurs de mon entreprise avec aperçu, et réinitialiser | 4 |
| US-20 | TenantAdmin | gérer les catégories et les SLA | 4 |
| US-21 | Développeur | rejouer un message de la dead-letter queue grâce à un runbook documenté | 3 |

## 8. Événements et files RabbitMQ

Cinq événements couvrent tout le MVP ; ils transitent par un exchange topic, chaque consommateur ayant sa propre file, avec nouvelles tentatives puis dead-letter queue.

**Topologie**

- Exchange `desk.events` (topic, durable) ; la routing key est le nom de l'événement.
- Une file durable `desk.<consommateur>` par consommateur, liée à `desk.events`.
- Exchange `desk.retry` et une file de délai (TTL) par consommateur et par palier, `desk.<consommateur>.retry.<délai>` (`5s`, `30s`, `2m`), qui renvoient le message vers la file d'origine par dead-lettering (une file par palier évite le blocage en tête de file ; voir ADR 008).
- Exchange `desk.dlx` et file `desk.dlq` pour les messages abandonnés.
- Messages JSON persistants, avec les en-têtes `message-id`, `correlation-id`, `tenant-id` et `attempt`.
- Publication avec publisher confirms ; consommation avec ack manuel et prefetch de 10.

| Événement (routing key) | Émis quand | Consommateurs | Effet |
| --- | --- | --- | --- |
| `ticket.created` | Création d'un ticket | `EmailAckConsumer`, `AgentRealtimeConsumer` (dans l'API) | E-mail d'accusé au demandeur ; notification temps réel aux agents |
| `ticket.assigned` | Assignation ou réassignation | `AssignmentNotifierConsumer`, `AgentRealtimeConsumer` | E-mail et notification temps réel à l'assigné |
| `ticket.commented` | Commentaire public ajouté | `CommentNotifierConsumer` | E-mail à l'autre partie (demandeur ou assigné) |
| `ticket.resolved` | Passage en `Resolved` | `KbDraftConsumer` | Crée un article `Draft` pré-rempli : titre du ticket, description en symptôme, dernier commentaire public en solution |
| `ticket.sla.breached` | Détecté par `SlaWatcher` | `EscalationConsumer` | Priorité relevée d'un cran, `EscalatedAt` renseigné, e-mail aux TenantAdmin, `TicketEvent` ajouté |

`SlaWatcher` est un `BackgroundService` du Worker : toutes les 60 secondes, il cherche les tickets non escaladés dont une échéance SLA est dépassée et publie `ticket.sla.breached` via l'outbox. L'opération est idempotente grâce à `EscalatedAt`.

**Fiabilité (exigée, testée et documentée)**

1. **Outbox** : le message est écrit dans la transaction métier ; un dispatcher le publie (polling toutes les secondes, `FOR UPDATE SKIP LOCKED`). Garantie : au moins une fois.
2. **Idempotence** : chaque consommateur enregistre `(MessageId, Consumer)` dans `ProcessedMessage` et ignore un doublon.
3. **Retries** : 3 nouvelles tentatives après la première livraison (délais de 5 s, 30 s puis 2 min, soit 4 livraisons au total), puis envoi en `desk.dlq` avec les en-têtes d'échec `x-*` (ADR 008).
4. **Message empoisonné** (JSON invalide) : envoyé directement en `desk.dlq`, sans nouvelle tentative.
5. **Rejeu** : une commande du Worker `replay-dlq` republie les messages de la DLQ vers leur exchange d'origine ; le runbook `docs/runbooks/dlq.md` explique comment inspecter la file dans l'interface de management (port 15672).
6. **Démonstration** : un réglage `Messaging:FailConsumer=<nom>` force un consommateur à échouer, pour montrer retries et DLQ en direct et pour les tests.
7. **Observabilité** : le `correlation-id` apparaît dans tous les logs ; le health check `/health/ready` vérifie PostgreSQL et RabbitMQ.

## 9. API REST

L'API est versionnée sous `/api/v1`, documentée par OpenAPI, et chaque route applique le rôle minimum indiqué ci-dessous.

| Méthode et route | Rôle minimum | Description |
| --- | --- | --- |
| `GET /auth/external/{provider}` | Public | Démarre la connexion Google ou GitHub |
| `POST /auth/refresh` | Cookie de refresh | Renvoie un access token |
| `POST /auth/logout` | Connecté | Révoque le refresh token |
| `POST /auth/demo` | Public, si activé | Connexion avec un compte de démo |
| `GET /me` | Connecté | Profil, rôle, entreprise et thème |
| `GET /tenants`, `POST /tenants`, `PATCH /tenants/{id}` | SuperAdmin | Gestion des entreprises |
| `GET /users`, `PATCH /users/{id}` | TenantAdmin | Lister, changer le rôle, désactiver |
| `GET /invitations`, `POST /invitations`, `DELETE /invitations/{id}` | TenantAdmin | Invitations par e-mail |
| `GET /categories` | Connecté | Liste des catégories |
| `POST /categories`, `PUT /categories/{id}`, `DELETE /categories/{id}` | TenantAdmin | Gestion des catégories |
| `GET /sla-policies`, `PUT /sla-policies` | TenantAdmin | Lire et modifier les SLA |
| `GET /theme` | Connecté | Couleurs de l'entreprise |
| `PUT /theme`, `DELETE /theme` | TenantAdmin | Modifier ou réinitialiser le thème |
| `GET /tickets` | Requester (les siens) ou Agent (tous) | Liste filtrée, triée, paginée |
| `POST /tickets` | Requester | Crée un ticket |
| `GET /tickets/{id}` | Requester (le sien) ou Agent | Détail avec commentaires |
| `PATCH /tickets/{id}` | Agent | Statut, priorité, assignation, catégorie |
| `POST /tickets/{id}/comments` | Requester ou Agent | Ajoute un commentaire ; `isInternal` réservé aux agents |
| `GET /tickets/{id}/events` | Requester (le sien) ou Agent | Historique d'audit |
| `GET /articles`, `GET /articles/{id}` | Connecté (brouillons : Agent) | Recherche et lecture |
| `POST /articles`, `PUT /articles/{id}` | Agent | Créer et modifier |
| `POST /articles/{id}/publish`, `POST /articles/{id}/unpublish` | Agent | Publication |
| `/hubs/notifications` | Connecté | Hub SignalR |
| `GET /health`, `GET /health/ready` | Public | Liveness et readiness |

**Conventions**

- JSON en camelCase ; erreurs au format RFC 7807 (`ProblemDetails`) ; validation avec FluentValidation (422).
- Pagination : `page` et `pageSize` (100 maximum) ; réponse `{ items, total, page, pageSize }`.
- Une ressource d'une autre entreprise renvoie 404, pas 403, pour ne rien révéler.
- Concurrence optimiste sur les tickets : le client renvoie `rowVersion`, un conflit renvoie 409.
- Les enums sont sérialisés en chaînes (`"Open"`, `"Urgent"`).

## 10. Interface Angular et thème de couleurs

L'interface est en français, responsive (mobile à desktop), et son identité repose sur trois couleurs que chaque entreprise peut remplacer : deux discrètes et une qui claque.

**Palette par défaut**

| Couleur | Nom | Hex | Usage |
| --- | --- | --- | --- |
| Discrète | Bleu ciel | `#7CC4EE` | Barre de navigation, éléments sélectionnés, liens secondaires, fonds de badges |
| Discrète | Blanc nuage | `#F4F7FA` | Fond de l'application, cartes, zones de saisie |
| Qui claque | Rouge ambré | `#D63A14` | Boutons d'action principaux, badges Urgent, alertes SLA, état actif important |

Texte principal `#14212B`, texte secondaire `#5B6B79`. Sur le bleu ciel, le texte est foncé (`#0B2A3C`) ; sur le rouge ambré, il est blanc. Un contraste d'au moins 4,5:1 est exigé pour tout texte.

**Règle d'usage** : environ 60 % de blanc nuage, 30 % de bleu ciel, 10 % de rouge ambré. L'accent ne couvre jamais de grandes surfaces, et un écran n'a qu'un seul bouton d'accent principal.

**Thème par entreprise**

- Trois champs : `ColorPrimary` (discrète, défaut bleu ciel), `ColorSurface` (discrète, défaut blanc nuage), `ColorAccent` (qui claque, défaut rouge ambré).
- L'écran de réglage affiche la consigne : "Choisissez deux couleurs discrètes et une couleur qui claque."
- Validation côté API et côté front : format `#RRGGBB` ; la couleur de texte (`on-*`) bascule automatiquement entre foncé et blanc selon le contraste calculé (WCAG) ; avertissement non bloquant si l'accent a moins de 3:1 de contraste avec la surface.
- Application : `GET /me` renvoie le thème, le front écrit les variables CSS sur `:root` (`--color-primary`, `--color-surface`, `--color-accent`, `--on-primary`, `--on-accent`) et dérive les teintes avec `color-mix()`. Aucun hex en dur dans les composants, aucune recompilation.
- L'écran de réglage propose un aperçu en direct (mini liste de tickets et un bouton) et un bouton "Réinitialiser".
- Le mode sombre est hors MVP.

**Choix techniques du front**

- Angular dernière version stable, TypeScript strict, composants standalone, signals, `inject()`, routes lazy par fonctionnalité.
- `HttpClient` typé avec intercepteurs (authentification, erreurs), guards par rôle, formulaires réactifs typés.
- Tailwind CSS avec variables CSS pour les couleurs, Angular CDK (overlay, dialog, a11y) ; pas de grosse bibliothèque de composants, pas de NgRx.
- ESLint et Prettier. Client TypeScript généré depuis l'OpenAPI si cela fait gagner du temps.

**Écrans**

| Écran | Route | Rôles |
| --- | --- | --- |
| Connexion (Google, GitHub, "Essayer la démo") | `/login` | Public |
| Retour d'authentification | `/auth/callback` | Public |
| Liste des tickets (filtres, tri, pagination) | `/tickets` | Requester (les siens), Agent |
| Nouveau ticket, avec suggestions d'articles | `/tickets/new` | Requester, Agent |
| Détail d'un ticket : fil de commentaires, statut, priorité, assigné, historique | `/tickets/:id` | Requester (le sien), Agent |
| Base de connaissances : liste, lecture, édition | `/kb`, `/kb/:id`, `/kb/:id/edit` | Connecté ; édition : Agent |
| Administration de l'entreprise : utilisateurs, invitations, catégories, SLA, thème | `/admin` | TenantAdmin |
| Plateforme : entreprises | `/platform/tenants` | SuperAdmin |
| Erreurs 403 et 404 | `/forbidden`, `**` | Tous |

**Qualité d'interface** : chaque liste a un état de chargement, un état vide et un état d'erreur ; navigation au clavier, labels de formulaire et focus visible partout.

## 11. Qualité, Docker Compose et CI

Le projet doit démarrer avec une seule commande, être testé sur ses points à risque, et ne jamais casser `main`.

**Docker Compose**

- `docker compose up` démarre PostgreSQL, RabbitMQ (avec l'interface de management), Mailpit, l'API, le Worker et le front (nginx qui sert aussi de reverse proxy vers l'API).
- Un fichier `.env.example` liste toutes les variables ; aucun secret réel n'est commité.
- Healthchecks sur chaque service et `depends_on` avec `condition: service_healthy`.
- Les migrations s'appliquent au démarrage de l'API en développement et en démo ; le seeder de démo s'exécute si `Demo:Enabled=true`.

**Tests**

| Niveau | Outils | À couvrir |
| --- | --- | --- |
| Unitaires back | xUnit | Transitions de statut, échéances SLA, validation du thème (contraste), règles par rôle |
| Intégration back | xUnit, `WebApplicationFactory`, Testcontainers (PostgreSQL, RabbitMQ) | Isolation entre entreprises ; création de ticket → message publié → e-mail reçu ; retries puis DLQ ; idempotence (même message deux fois = un seul effet) ; rotation du refresh token |
| Front | Lanceur de tests par défaut de la version d'Angular | Services, guards, calcul du thème, composants clés |
| E2E (phase 5) | Playwright | Connexion démo → création d'un ticket → réponse d'un agent → résolution → brouillon d'article |

**CI GitHub Actions**

- `ci.yml` à chaque pull request et chaque push sur `main` : build et tests .NET (Docker disponible pour Testcontainers), lint, build et tests Angular, `docker build` des images. Cache NuGet et npm.
- `main` est protégée : pull request obligatoire et CI verte avant fusion.
- `deploy.yml` (phase 5) déploie la démo depuis `main`.

**Standards**

- `.editorconfig`, `dotnet format --verify-no-changes`, types nullables activés, ESLint, TypeScript strict.
- Logs Serilog en JSON avec `correlation-id`.
- Aucun secret dans le dépôt ; un scan de secrets (par exemple gitleaks) en CI est un plus.

## 12. Documentation et journal de troubleshooting

La documentation fait partie du livrable : c'est ce qu'un recruteur ou un tech lead lit en premier. Deux choses distinctes sont documentées.

- **La base de connaissances du produit** est une fonctionnalité : des articles Symptôme / Cause / Solution pour les utilisateurs du helpdesk. Pour la démo, un seeder idempotent crée des données fictives : 12 articles et 30 tickets (VPN, imprimante, mot de passe expiré, messagerie, accès à un partage réseau, etc.), tous marqués "données fictives".
- **Le journal de troubleshooting du projet** est de la documentation : les problèmes réels rencontrés pendant le développement, avec leur cause et leur solution.

**Arborescence**

| Fichier | Contenu |
| --- | --- |
| `README.md` | Pitch en 3 lignes, capture ou GIF, lien de la démo, fonctionnalités, stack, démarrage en 3 commandes, schéma d'architecture (Mermaid), liens vers la doc |
| `docs/architecture.md` | Composants, flux, topologie RabbitMQ, isolation multi-entreprise |
| `docs/adr/NNN-titre.md` | Une décision par fichier : monolithe modulaire, base partagée + `TenantId`, client RabbitMQ officiel plutôt que MassTransit v9, outbox, refresh token en cookie + JWT en mémoire, thème par variables CSS |
| `docs/troubleshooting/NNN-titre.md` | Une entrée par problème résolu, plus un `README.md` d'index (numéro, titre, composant) |
| `docs/runbooks/dlq.md` | Inspecter et rejouer la dead-letter queue |
| `docs/setup-oauth.md` | Créer les identifiants Google et GitHub, URI de redirection |
| `docs/demo.md` | Scénario de démonstration de 5 minutes |

**Modèle d'une entrée de troubleshooting**

```markdown
# NNN — Titre court du problème
Date : AAAA-MM-JJ · Phase : N · Composant : API | Worker | Web | Docker | CI · Lien : PR ou commit

## Symptôme
Ce qu'on observe : message d'erreur, comportement.

## Contexte
Ce qui était en cours, versions, environnement.

## Cause racine
Pourquoi cela arrivait.

## Solution
Ce qui a été fait, avec un extrait de code ou de configuration si utile.

## Pour l'éviter
Test ajouté, règle, vérification.
```

**Règles pour Claude Code**

- Dès qu'un problème prend plus de 15 minutes à résoudre ou touche l'infrastructure (Docker, OAuth, RabbitMQ, migrations, CI), l'entrée de troubleshooting est écrite dans le même commit que la correction.
- Uniquement des problèmes réellement rencontrés, jamais inventés : le journal doit pouvoir être raconté en entretien.
- Entrées courtes et factuelles ; l'index `docs/troubleshooting/README.md` est mis à jour à chaque ajout.
- Chaque décision d'architecture non triviale donne un ADR court (contexte, décision, conséquences).

## 13. Plan de livraison

Le MVP est livré à la fin de la phase 3, soit environ 10 jours de travail effectif ; les phases 4 et 5 amènent le projet à un état présentable en portfolio, en 3 semaines environ. Ces durées sont des estimations à ajuster après la phase 0.

| Phase | Objectif | Contenu | Porte de sortie |
| --- | --- | --- | --- |
| 0. Fondations (1 j) | Une base saine | Monorepo, solution .NET (4 projets + tests), squelette Angular, Docker Compose (postgres, rabbitmq, mailpit), CI minimale, health checks, README initial, ADR 001 à 003, variables CSS de la palette par défaut | `docker compose up` fonctionne, CI verte, `/health/ready` répond |
| 1. Identité et multi-entreprise (2 à 3 j) | Se connecter et être isolé | EF Core, entités de la section 6 liées à l'identité, OAuth Google et GitHub, JWT + refresh, invitations, connexion de démo, filtres de tenant, écran de connexion, gestion minimale des utilisateurs | US-01 à US-04 ; tests d'isolation verts |
| 2. Tickets (3 à 4 j) | Le cœur métier | API tickets, commentaires, catégories, historique, transitions ; écrans liste, détail, création | US-05 à US-11 |
| 3. RabbitMQ (3 à 4 j) | Le MVP asynchrone | Couche RabbitMQ, outbox, consommateurs d'e-mail, `SlaWatcher` et escalade, brouillon d'article, SignalR, retries, DLQ, `replay-dlq`, runbook | US-12 à US-16 et US-21 ; tests de retry, DLQ et idempotence verts. **MVP, tag `v0.1.0`** |
| 4. Base de connaissances, thème, admin (3 à 4 j) | Finir les fonctionnalités | Édition et publication d'articles, recherche, suggestions à la saisie, écran de thème avec aperçu, catégories et SLA | US-17 à US-20 ; tag `v0.2.0` |
| 5. Finition et démo (2 à 3 j) | Rendre le projet présentable | Seeder de démo, test E2E Playwright, déploiement, README avec captures, `docs/demo.md`, relecture du journal de troubleshooting | Critères de la section 15 ; tag `v1.0.0` |

**Hébergement de la démo** : à choisir en phase 5 par Babacar. Deux pistes : un VPS avec Docker Compose, ou un PaaS avec PostgreSQL et RabbitMQ managés (offres gratuites à vérifier à ce moment-là).

**Si le temps manque**, on coupe dans cet ordre : suggestions à la saisie (US-18), SignalR (le reste de US-13 demeure), test E2E, puis la recherche d'articles. Le multi-entreprise, RabbitMQ avec retry et DLQ, l'authentification Google et GitHub et la documentation ne se coupent pas : ils sont la raison d'être du projet.

## 14. Règles de travail pour Claude Code

Claude Code livre en petites pull requests, teste ce qu'il écrit, ne touche jamais `main` directement, et documente les problèmes réels qu'il résout.

**Avant de démarrer (actions manuelles de Babacar)**

1. Créer le dépôt GitHub vide, le cloner, et y placer ce cahier des charges sous `docs/SPEC.md` (export Markdown du doc).
2. Installer le SDK .NET 10, Node.js LTS, Docker Desktop, et authentifier GitHub CLI (`gh auth login`).
3. Au début de la phase 1 : créer l'application OAuth Google et l'OAuth App GitHub, puis renseigner les secrets (`docs/setup-oauth.md`).
4. Valider et fusionner chaque pull request, sauf instruction contraire.
5. Choisir l'hébergement de la démo en phase 5.

**Git et pull requests**

- Développement trunk-based ; branches `feat/<phase>-<sujet>`, `fix/…`, `docs/…`, `chore/…` ; jamais de push direct sur `main`.
- Commits en anglais au format Conventional Commits (`feat(api): add ticket status transitions`), petits et fréquents.
- Une pull request par tranche verticale (base de données, API, interface, tests) ; fusion en squash ; tag `v0.x.0` à la fin des phases 3, 4 et 5.
- Description de la pull request : Quoi, Pourquoi, Comment tester, Captures (si interface), User stories couvertes, entrée de troubleshooting éventuelle.
- La CI doit être verte avant toute demande de fusion. À la fin d'une phase, la pull request résume : fait, reste, risques.

**Qualité et sécurité**

- Travailler par tranches verticales, pas couche par couche.
- Chaque fonctionnalité arrive avec ses tests ; ne jamais supprimer ni affaiblir un test pour faire passer la CI.
- Aucun secret dans le dépôt ; le filtre de tenant n'est désactivé que dans les endpoints SuperAdmin ; toute validation est refaite côté serveur.
- Vérifier les versions stables courantes avant d'installer (.NET 10 LTS, dernière version stable d'Angular, `RabbitMQ.Client`), puis les épingler.

**Porte qualité** : aucune pull request n'est ouverte sans le rapport de l'`auditor` décrit en section 16, collé dans la description de la pull request.

**Décisions et questions**

- Quand ce document ne tranche pas, choisir l'option la plus simple et écrire un ADR court.
- Ne poser une question que si elle bloque : identifiants OAuth, secrets, hébergement. Continuer sur le reste en attendant.
- Si le cahier des charges doit changer, modifier `docs/SPEC.md` dans la même pull request.

**Prompt de démarrage pour une seule session (avec l'équipe d'agents, utiliser celui de la section 16)**

```
Lis docs/SPEC.md en entier. Tu es chargé de livrer le MVP de DeskFlow en suivant la section 13.
Commence par la phase 0 : crée la branche feat/phase-0-foundations, mets en place le monorepo,
Docker Compose, la CI minimale, les health checks, le README initial et les ADR 001 à 003.
Respecte la section 14 (git, pull requests, tests, troubleshooting).
Ouvre une pull request à la fin de la phase avec : fait, reste, risques, puis arrête-toi
pour ma validation. Si une action manuelle de ma part est nécessaire (identifiants OAuth,
secrets), dis-le clairement et continue sur le reste.
```

## 15. Critères d'acceptation

Une user story est terminée quand son code est fusionné sur `main`, ses tests passent, la documentation concernée est à jour et la CI est verte. Le projet est terminé quand toutes les cases ci-dessous sont cochées.

- [ ] `docker compose up` sur une machine propre lance toute la pile ; l'application est accessible sans autre manipulation que le fichier `.env`.
- [ ] La connexion Google et la connexion GitHub fonctionnent ; sans invitation, l'accès est refusé avec un message clair.
- [ ] Avec deux entreprises de test, aucune donnée ne fuit de l'une à l'autre (tests d'isolation verts).
- [ ] Créer un ticket déclenche un e-mail d'accusé visible dans Mailpit en moins de 5 secondes.
- [ ] Avec `Messaging:FailConsumer` activé, un message passe par 3 tentatives puis arrive dans `desk.dlq` ; `replay-dlq` le rejoue.
- [ ] Un même message reçu deux fois ne produit qu'un seul effet.
- [ ] Un ticket hors SLA est escaladé une seule fois.
- [ ] Résoudre un ticket crée un brouillon d'article pré-rempli.
- [ ] Modifier les 3 couleurs dans l'administration change l'interface sans rechargement ni redéploiement ; "Réinitialiser" restaure bleu ciel, blanc nuage et rouge ambré.
- [ ] La CI est verte sur `main`, qui est protégée.
- [ ] Le README contient une capture ou un GIF, le schéma d'architecture, le démarrage en 3 commandes et le lien de la démo.
- [ ] Chaque difficulté réelle de plus de 15 minutes a son entrée dans `docs/troubleshooting` ; aucune entrée n'est inventée.
- [ ] La démo est déployée et utilisable avec les comptes de démo (admin, agent, demandeur).

* [ ] Chaque pull request fusionnée contient le rapport de l'`auditor`, sans point Bloquant ni Majeur ouvert.

## 16. Équipe d'agents (orchestrateur + 3 sous-agents)

Claude Code travaille avec quatre rôles : l'orchestrateur est la session principale, et trois sous-agents (backend, frontend, audit et correction) sont définis dans `.claude/agents/`. Un sous-agent ne peut en principe pas en lancer d'autres, d'où le choix de l'orchestrateur comme session principale plutôt que comme fichier d'agent.

**Principe qualité** : les skills et les sous-agents sont tous deux conservés, car ils se complètent. Les skills (hallmark pour l'interface) apportent des règles de métier ; les sous-agents apportent la séparation des rôles et une revue indépendante. Rien n'est fusionné sans passer par les deux. D'autres skills du même type peuvent s'ajouter : l'orchestrateur lit leur `SKILL.md` avant de les activer et note le choix dans un ADR.

| Agent | Rôle | Écrit dans | Outils et modèle suggérés |
| --- | --- | --- | --- |
| Orchestrateur (session principale) | Lit `docs/SPEC.md`, découpe chaque phase en tâches, délègue, intègre, tient les ADR et le journal de troubleshooting, ouvre les pull requests | `docs/`, `docker-compose.yml`, `.github/`, `.claude/` | Le modèle le plus fort disponible |
| `backend` | API, Worker, EF Core, RabbitMQ, tests .NET | `src/`, `tests/` | Read, Write, Edit, Bash, Grep, Glob ; `model: sonnet` |
| `frontend` | Angular, écrans, thème ; applique le skill hallmark | `web/` | Read, Write, Edit, Bash, Grep, Glob ; `model: sonnet` |
| `auditor` | Revue indépendante, puis corrections mineures | Lecture partout ; écriture limitée aux corrections mineures | Read, Grep, Glob, Bash, Edit ; modèle fort |

**Règles de collaboration**

1. **Contrat d'abord.** Avant de déléguer en parallèle, le `backend` produit le contrat `docs/api/openapi.json` de la tranche ; le `frontend` s'appuie dessus (client généré ou données simulées). Tout changement de contrat passe par l'orchestrateur.
2. **Propriété des dossiers.** Un agent n'écrit que dans ses dossiers. Les fichiers partagés (compose, CI, docs) appartiennent à l'orchestrateur, ce qui évite presque tous les conflits de fusion.
3. **Parallélisme.** `backend` et `frontend` travaillent en parallèle une fois le contrat figé, chacun sur sa branche (`feat/p2-backend-tickets`, `feat/p2-frontend-tickets`), dans des git worktrees séparés. L'`auditor` passe après chaque tranche, jamais en même temps qu'un auteur sur le même code.
4. **Rôle de l'auditor.** Il vérifie les critères de la section 15, l'isolation entre entreprises, les secrets, les transitions de statut et le SLA, l'idempotence, la présence des tests, l'accessibilité et le contraste du thème, et lance `hallmark audit web/`. Son rapport classe chaque point en Bloquant, Majeur ou Mineur et va dans la pull request.
5. **Corrections.** L'`auditor` corrige lui-même uniquement les points Mineurs (formatage, nommage, test trivial manquant, texte). Les Bloquants et Majeurs retournent à l'agent propriétaire via l'orchestrateur : un agent ne corrige pas son propre travail.
6. **Troubleshooting.** Un sous-agent qui résout un vrai problème le signale dans son résumé final (symptôme, cause, solution) ; l'orchestrateur écrit l'entrée correspondante (section 12).
7. **Coût.** Chaque sous-agent a son propre contexte et consomme des tokens. Les implémenteurs tournent en `sonnet`, l'orchestrateur et l'auditeur gardent le modèle le plus fort, et on ne lance pas les quatre en continu. Si l'auditor relève des défauts récurrents chez un implémenteur, cet agent passe au modèle fort : la qualité prime sur le coût.

**Porte qualité (obligatoire avant toute pull request)**

1. L'agent auteur lance build, tests, lint et format, et corrige les échecs.
2. L'`auditor` audite la tranche et publie son rapport, avec chaque point classé Bloquant, Majeur ou Mineur.
3. Les points Bloquants et Majeurs retournent à l'agent propriétaire via l'orchestrateur ; l'`auditor` corrige les points Mineurs.
4. L'`auditor` audite à nouveau. Après deux boucles sans résolution, l'orchestrateur s'arrête et demande une décision à Babacar.
5. La pull request n'est ouverte que si aucun point Bloquant ou Majeur n'est ouvert ; le rapport est collé dans sa description.

**Skill hallmark (frontend)**

- Installation à la racine du dépôt : `npx skills add nutlope/hallmark --skill hallmark --agent claude-code`, qui place le skill dans `.claude/skills/`. Lire son `SKILL.md` avant de l'activer : c'est une instruction que les agents suivront.
- La palette de la section 10 est verrouillée. Le `frontend` crée d'abord `web/src/styles/tokens.css` (variables `--color-primary`, `--color-surface`, `--color-accent`, `--on-primary`, `--on-accent`) et un `design.md` qui le décrit ; hallmark applique ces tokens au lieu de choisir son propre thème.
- Le skill est déclaré dans le fichier d'agent `frontend` ; l'`auditor` utilise sa commande `audit`.

**Fichiers d'agents (à créer par l'orchestrateur en phase 0)**

```markdown
# .claude/agents/backend.md
---
name: backend
description: Implémente l'API, le Worker, EF Core, RabbitMQ et les tests .NET de DeskFlow. À utiliser pour toute tâche dans src/ ou tests/.
tools: Read, Write, Edit, Bash, Grep, Glob
model: sonnet
---
Tu es l'agent backend de DeskFlow. Source de vérité : docs/SPEC.md. Tu n'écris que dans src/ et tests/.
Chaque fonctionnalité arrive avec ses tests. Termine par un résumé court : fichiers modifiés, tests lancés,
problèmes réels rencontrés (symptôme, cause, solution).

# .claude/agents/frontend.md
---
name: frontend
description: Implémente l'application Angular, les écrans et le thème par entreprise. À utiliser pour toute tâche dans web/.
tools: Read, Write, Edit, Bash, Grep, Glob
model: sonnet
skills:
  - hallmark
---
Tu es l'agent frontend de DeskFlow. Source de vérité : docs/SPEC.md (sections 7, 10). Tu n'écris que dans web/.
Tu utilises les tokens de la palette verrouillée, aucun hex en dur. Termine par un résumé court, avec les problèmes réels rencontrés.

# .claude/agents/auditor.md
---
name: auditor
description: Audit indépendant d'une tranche terminée : conformité au SPEC, sécurité, isolation des entreprises, tests, accessibilité. Corrige seulement les points mineurs.
tools: Read, Grep, Glob, Bash, Edit
---
Tu es l'auditeur de DeskFlow. Tu ne réécris pas le travail des autres. Produis un rapport classé Bloquant / Majeur / Mineur.
Corrige uniquement les points Mineurs, renvoie le reste à l'orchestrateur avec le nom de l'agent propriétaire.
```

Le format exact des champs (en particulier `skills`) se vérifie avec la commande `/agents` de Claude Code ; en cas d'écart, c'est la documentation de Claude Code qui fait foi.

**Prompt de démarrage de l'orchestrateur**

```
Tu es l'orchestrateur. Lis docs/SPEC.md en entier (sections 13 à 16).
Phase 0 : fais-la toi-même sur feat/phase-0-foundations (monorepo, Docker Compose, CI, health checks,
README, ADR 001 à 003) et crée .claude/agents/ avec backend, frontend et auditor selon la section 16.
À partir de la phase 1 : fige d'abord le contrat OpenAPI, délègue backend et frontend en parallèle sur
des branches séparées, lance l'auditor sur chaque tranche, renvoie les points Bloquants et Majeurs au
propriétaire, puis ouvre les pull requests, mais seulement après un rapport de l'auditor sans point Bloquant ni Majeur ouvert (porte qualité, section 16). Respecte la section 14.
Arrête-toi à la fin de chaque phase pour ma validation.
```
