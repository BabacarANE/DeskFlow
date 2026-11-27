# Démonstration de 5 minutes

Pré-requis : `docker compose up --build` terminé (tous les services sains), `DEMO_ENABLED=true` (valeur par défaut). L'application est sur http://localhost:8080, les e-mails sur http://localhost:8025 (Mailpit), RabbitMQ sur http://localhost:15672. Les données sont fictives (bandeau « Mode démo, données fictives »). Astuce : ouvrir deux fenêtres, l'une en navigation privée, pour tenir deux rôles en même temps (chaque session est indépendante).

## 1. Le demandeur crée un ticket (1 min)

1. Page de connexion → profil **Demandeur** → « Essayer la démo ».
2. **Nouveau ticket** : tapez « imprimante » dans le titre. Après une courte pause, des **articles de la base de connaissances** sont suggérés (la recherche ignore accents et casse).
3. Validez la création, puis ouvrez Mailpit : l'**e-mail d'accusé de réception** est arrivé en quelques secondes. Il a transité par l'outbox, RabbitMQ et le consommateur `email-ack`.

## 2. L'agent traite le ticket (1 min 30)

1. Autre fenêtre : profil **Agent**. La liste contient 30 tickets de démonstration plus le vôtre ; filtrez par statut, priorité ou assigné.
2. Ouvrez votre ticket → **M’assigner** → **Passer à « Ouvert »**, puis écrivez une **réponse publique**. Le demandeur reçoit un e-mail (Mailpit) ; une **note interne** reste invisible pour lui.
3. Cliquez la **cloche** : les événements arrivent en temps réel (SignalR).
4. **Passer à « Résolu ».**

## 3. Un brouillon d'article apparaît (30 s)

Toujours en agent : **Base de connaissances** → filtre de statut « Brouillons ». Le ticket résolu a produit un **brouillon pré-rempli** (symptôme, solution) lié au ticket source. Éditez-le puis **Publier** : il devient visible du demandeur.

## 4. L'administrateur change le thème (1 min)

Profil **Administrateur** → **Administration** → onglet **Thème**. Changez les trois couleurs : l'**aperçu** réagit sans rien enregistrer. Un avertissement apparaît si l'accent contraste trop peu avec le fond. **Enregistrer le thème** : toute l'interface change sans rechargement. **Réinitialiser** restaure bleu ciel, blanc nuage et rouge ambré. Dans les onglets SLA et Catégories, l'administrateur règle aussi les délais par priorité et les catégories.

## 5. Quand ça tombe en panne : retries, DLQ, rejeu (1 min 30)

Dans un terminal, forcez l'échec d'un consommateur :

```bash
# dans .env : MESSAGING_FAIL_CONSUMER=email-ack
docker compose up -d worker
```

Créez un ticket : l'e-mail n'arrive pas. Dans RabbitMQ (Queues), le message passe par `desk.email-ack.retry.5s`, `.30s`, `.2m` (environ 2 min 35 s au total), puis arrive dans **`desk.dlq`** avec les en-têtes de diagnostic. Corrigez la cause (retirez la variable, `docker compose up -d worker`) puis rejouez :

```bash
docker compose exec worker dotnet Desk.Worker.dll replay-dlq --dry-run   # voir ce qui serait rejoué
docker compose exec worker dotnet Desk.Worker.dll replay-dlq
```

L'e-mail arrive dans Mailpit. Détails : [runbook DLQ](runbooks/dlq.md).

## À dire en entretien

- **Isolation** : base partagée, `TenantId`, filtres globaux, 404 pour une autre entreprise ; prouvée par des tests avec deux entreprises ([ADR 002](adr/002-shared-database-tenant-id.md)).
- **Fiabilité** : outbox transactionnelle, idempotence, reprises, DLQ, rejeu, consommateurs supervisés qui se réabonnent après une coupure ([ADR 008](adr/008-messaging-layer-design.md)).
- **Ce qui a mal tourné** : 27 entrées dans le [journal de troubleshooting](troubleshooting/README.md), toutes réellement rencontrées (par exemple l'outbox qui marquait « envoyé » un événement non routable).
- **Limite honnête** : les connexions Google et GitHub n'ont pas été éprouvées de bout en bout ici ; le mode démo est le parcours testé.

## Remarque sur l'escalade SLA

Quelques tickets de démonstration approchent de leur échéance : en laissant la démo tourner, le `SlaWatcher` peut escalader l'un d'eux une seule fois et envoyer un e-mail à l'administrateur (visible dans Mailpit). C'est voulu.
