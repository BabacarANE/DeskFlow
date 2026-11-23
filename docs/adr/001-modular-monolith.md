# ADR 001 — Monolithe modulaire (API + Worker)

Statut : accepté · Date : 2026-11-23

## Contexte
DeskFlow doit être livré vite et démontrer un vrai flux asynchrone. Des microservices multiplieraient le coût d'exploitation (déploiement, contrats, observabilité) sans bénéfice pour un MVP.

## Décision
Un monolithe modulaire : `Desk.Core` (domaine, contrats d'événements), `Desk.Infrastructure` (EF Core, outbox, RabbitMQ, e-mail), `Desk.Api` (Web API, auth, SignalR) et `Desk.Worker` (consommateurs, SLA, dispatcher outbox). L'API et le Worker sont deux processus séparés qui partagent le code via `Desk.Core` et `Desk.Infrastructure`.

Le Worker utilise le SDK Web afin d'exposer `/health` et `/health/ready` pour le healthcheck Docker, avec le même code que l'API.

## Conséquences
- Un seul dépôt et une seule solution ; l'API et le Worker se déploient et se mettent à l'échelle séparément.
- Les frontières entre modules reposent sur la discipline de dépendance (`Core` ne référence rien), pas sur le réseau.
- Un découpage en services reste possible plus tard, le long des files RabbitMQ.
