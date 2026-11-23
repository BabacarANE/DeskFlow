# Runbook — inspecter et rejouer la dead-letter queue

Quand un message échoue après toutes ses reprises (ou qu'il est illisible), il arrive dans `desk.dlq`. Ce runbook explique comment le comprendre, corriger la cause, puis le rejouer. Conception : ADR 008.

## 1. Repères

| Élément | Nom |
| --- | --- |
| Exchanges | `desk.events` (topic), `desk.retry`, `desk.dlx` |
| File des messages abandonnés | `desk.dlq` |
| File d'un consommateur | `desk.<consommateur>` (ex. `desk.email-ack`) |
| Files de délai | `desk.<consommateur>.retry.5s`, `.30s`, `.2m` |
| Interface de management | http://localhost:15672 (identifiants du `.env` : `RABBITMQ_USER`, `RABBITMQ_PASSWORD`) |

Un message passe par 4 livraisons (immédiate, puis après 5 s, 30 s et 2 min), soit 2 min 35 s, avant la DLQ. Un message illisible (JSON ou enveloppe invalide, tenant manquant, ticket introuvable dans le tenant du message) y va directement, sans reprise.

## 2. Inspecter

1. Management → onglet **Queues** → `desk.dlq` → **Get messages** (mode « Nack message requeue true » pour ne rien retirer).
2. Lire les en-têtes du message :
   - `x-failed-consumer` : le consommateur qui a échoué ;
   - `x-failure-reason` : type d'exception et message ;
   - `x-failure-kind` : `retries-exhausted` (échec répété) ou `poison` (message inexploitable) ;
   - `x-failed-at`, `attempt`, `x-original-exchange`, `x-original-routing-key` ;
   - `message-id`, `correlation-id`, `tenant-id`.
3. Chercher le `correlation-id` dans les logs JSON de l'API et du Worker : `docker compose logs api worker | grep <correlation-id>`.

## 3. Corriger la cause

Rejouer sans corriger renvoie le message en DLQ. Selon `x-failure-reason` : SMTP injoignable (vérifier Mailpit ou le serveur SMTP), base indisponible, donnée absente. Un message `poison` ne guérit pas en le rejouant : l'analyser, puis le purger depuis le management s'il est irrécupérable.

## 4. Rejouer

```bash
# Simulation : liste ce qui serait rejoué, sans rien publier
docker compose exec worker dotnet Desk.Worker.dll replay-dlq --dry-run

# Rejeu (option --max N pour limiter le nombre de messages)
docker compose exec worker dotnet Desk.Worker.dll replay-dlq
```

En local, hors Docker : `dotnet src/Desk.Worker/bin/Debug/net10.0/Desk.Worker.dll replay-dlq` avec `ConnectionStrings__RabbitMq` défini.

La commande republie chaque message sur `desk.events` avec sa clé d'origine (même `message-id` et `correlation-id`, `attempt` remis à 0, `x-replay-count` incrémenté), ne le retire de la DLQ qu'après confirmation du broker et affiche un compte rendu. Codes de sortie : 0 succès (y compris DLQ vide), 1 au moins un message ignoré ou en échec (il reste dans la DLQ), 2 arguments invalides, 3 broker injoignable.

Le message est livré à **tous** les consommateurs liés à la clé : l'idempotence (`ProcessedMessage`) fait ignorer le doublon à ceux qui l'ont déjà traité, donc l'effet n'a lieu qu'une fois.

## 5. Démonstration

```bash
# 1. Forcer l'échec d'un consommateur (dans .env), puis relancer le worker
MESSAGING_FAIL_CONSUMER=email-ack
docker compose up -d worker

# 2. Créer un ticket : le message est repris à 5 s, 30 s et 2 min, puis arrive dans desk.dlq
# 3. Retirer MESSAGING_FAIL_CONSUMER, relancer le worker, puis :
docker compose exec worker dotnet Desk.Worker.dll replay-dlq
# 4. L'e-mail d'accusé apparaît dans Mailpit (http://localhost:8025)
```
