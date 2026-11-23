# ADR 003 — Client RabbitMQ officiel plutôt que MassTransit v9

Statut : accepté · Date : 2026-11-23

## Contexte
MassTransit v9 est un produit commercial sous licence ; seule la v8 reste en Apache 2.0, avec des correctifs de sécurité annoncés jusqu'à fin 2026. Un nouveau projet ne doit pas dépendre d'une branche en fin de vie.

## Décision
Utiliser `RabbitMQ.Client` (API asynchrone, version 7.x) avec une petite couche maison : déclaration de la topologie, publication avec confirmations, consommateurs avec ack manuel, retries par files TTL, dead-letter queue, outbox et idempotence.

## Conséquences
- Aucune licence ni dépendance à une branche non maintenue.
- Exchanges, bindings, acks, DLX et prefetch sont visibles dans le code, ce qui sert la démonstration.
- Nous écrivons et testons nous-mêmes la fiabilité (retries, DLQ, idempotence) : coût accepté, couvert par des tests Testcontainers en phase 3.
