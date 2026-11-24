# Contrat des événements et du temps réel (phase 3)

Document gelé par l'orchestrateur, au même titre que `docs/api/openapi.json` : tout changement passe par lui. Sources : SPEC sections 3, 7 (US-12 à US-16), 8.

## 1. Enveloppe des messages RabbitMQ

Exchange `desk.events` (topic, durable) ; la routing key est le nom de l'événement. Message JSON persistant, en camelCase :

```json
{
  "messageId": "uuid",
  "type": "ticket.created",
  "tenantId": "uuid",
  "occurredAt": "2026-11-23T10:00:00.000000Z",
  "correlationId": "string",
  "payload": { }
}
```

En-têtes AMQP obligatoires : `message-id` (= `messageId`), `correlation-id`, `tenant-id` (= `tenantId`), `attempt` (entier, 0 à la première livraison). `tenantId` n'est jamais nul. Les identifiants de l'enveloppe sont les mêmes dans l'outbox (`OutboxMessage.Id` = `messageId`).

Un consommateur positionne le tenant avant tout accès aux données, charge les entités par identifiant (les payloads ne contiennent ni e-mail ni corps de commentaire) et n'écrit jamais hors de son entreprise.

## 2. Événements (SPEC section 8)

| Routing key | Émis par | Payload |
| --- | --- | --- |
| `ticket.created` | création d'un ticket | `{ ticketId, number, title, priority, requesterId }` |
| `ticket.assigned` | assignation ou réassignation (assignee non nul) | `{ ticketId, number, title, assigneeId, previousAssigneeId \| null, actorId }` |
| `ticket.commented` | commentaire **public** ajouté (jamais une note interne) | `{ ticketId, number, commentId, authorId }` |
| `ticket.resolved` | passage en `Resolved` | `{ ticketId, number, resolvedAt }` |
| `ticket.sla.breached` | `SlaWatcher` | `{ ticketId, number, kind: "response" \| "resolution", dueAt }` |

Les valeurs de `priority` sont les chaînes de l'API (`Low`, `Normal`, `High`, `Urgent`).

## 3. Files et consommateurs

| Consommateur | File | Routing keys liées | Héberge |
| --- | --- | --- | --- |
| `EmailAckConsumer` | `desk.email-ack` | `ticket.created` | Worker |
| `AssignmentNotifierConsumer` | `desk.assignment-notifier` | `ticket.assigned` | Worker |
| `CommentNotifierConsumer` | `desk.comment-notifier` | `ticket.commented` | Worker |
| `KbDraftConsumer` | `desk.kb-draft` | `ticket.resolved` | Worker |
| `EscalationConsumer` | `desk.escalation` | `ticket.sla.breached` | Worker |
| `AgentRealtimeConsumer` | `desk.agent-realtime` | `ticket.created`, `ticket.assigned`, `ticket.commented` | API (le hub SignalR y vit) |

`ticket.commented` est aussi lié à `AgentRealtimeConsumer` : cela réconcilie US-13 (« notifié quand le demandeur répond ») avec le tableau de la section 8 ; le consommateur ne notifie en temps réel que l'assigné, et seulement si l'auteur est le demandeur.

Retries, dead-letter queue et idempotence : SPEC section 8 (3 tentatives à 5 s, 30 s, 2 min, puis `desk.dlq` ; JSON invalide directement en `desk.dlq` ; `ProcessedMessage` sur `(MessageId, Consumer)`).

## 4. Effets métier

- **`EmailAckConsumer`** : e-mail d'accusé de réception au demandeur (visible dans Mailpit en moins de 5 s).
- **`AssignmentNotifierConsumer`** : e-mail à l'assigné.
- **`CommentNotifierConsumer`** : e-mail à l'autre partie (le demandeur si l'auteur est un agent, l'assigné si l'auteur est le demandeur ; rien s'il n'y a pas d'assigné).
- **`KbDraftConsumer`** : crée un `KnowledgeArticle` au statut `Draft`, `SourceTicketId` = le ticket : titre = titre du ticket, `Symptom` = description, `Solution` = dernier commentaire public (vide s'il n'y en a pas), `Cause` vide, `AuthorId` = l'assigné (ou le demandeur à défaut). Un seul brouillon par ticket (idempotent même après réouverture puis nouvelle résolution : un brouillon existant n'est pas dupliqué).
- **`EscalationConsumer`** : relève la priorité d'un cran (`Urgent` reste `Urgent`, sans `TicketEvent`), renseigne `EscalatedAt`, ajoute un `TicketEvent` (`PriorityChanged`, acteur nul) quand la priorité change ; un ticket qui n'est plus `New` ou `Open` n'est pas escaladé, recalcule les échéances SLA, e-mail aux TenantAdmin de l'entreprise. Idempotent grâce à `EscalatedAt` : un ticket n'est escaladé qu'une fois.
- **`SlaWatcher`** (`BackgroundService` du Worker, toutes les 60 s) : pour les tickets en `New` ou `Open`, non escaladés, dont `ResolutionDueAt` est dépassé, ou `ResponseDueAt` dépassé sans `FirstResponseAt` ; publie `ticket.sla.breached` via l'outbox.

## 5. Temps réel (SignalR)

Hub `/hubs/notifications`, authentifié par le JWT (`access_token` en query string pour le transport WebSocket). Groupes gérés par le serveur à la connexion  : `user:{userId}` et `agents:{tenantId}`, pour les rôles Agent et TenantAdmin seulement (un Requester ou le SuperAdmin est refusé à la connexion : 403). Aucun événement ne traverse les entreprises.

Événements serveur vers client (charges en camelCase) :

| Méthode cliente | Destinataires | Charge |
| --- | --- | --- |
| `ticketCreated` | groupe `agents:{tenantId}` | `{ ticketId, number, title, priority, at }` |
| `ticketAssigned` | groupe `user:{assigneeId}` | `{ ticketId, number, title, at }` |
| `ticketReplied` | groupe `user:{assigneeId}` (l'auteur est le demandeur) | `{ ticketId, number, title, at }` |

Le client n'appelle aucune méthode serveur. La perte de connexion n'efface rien : l'état fait foi dans l'API REST.

## 6. Réglages

- `Messaging:FailConsumer=<nom>` : force le consommateur nommé à échouer (démonstration des retries et de la DLQ, tests).
- Commande du Worker `replay-dlq` : republie les messages de `desk.dlq` vers leur exchange d'origine (runbook `docs/runbooks/dlq.md`, rédigé par l'orchestrateur).
- Connexion : `ConnectionStrings:RabbitMq` (déjà en place).

## 7. Précisions de la tranche B
- Le marqueur « une seule émission » de `ticket.sla.breached` est la table `SlaBreaches` (clé `(TicketId, Kind)`), écrite dans la transaction de l'outbox (ADR 009). Si les deux échéances sont dépassées, `response` et `resolution` sont émis ; le second est sans effet grâce à `EscalatedAt`.
- Le brouillon d'article reprend verbatim le titre et la description du ticket : il peut contenir des données personnelles saisies par le demandeur ; il reste au statut `Draft` et sans endpoint avant la phase 4.
- Le `/health/ready` de l'API n'inclut pas le consommateur temps réel ; celui du Worker inclut ses consommateurs.
