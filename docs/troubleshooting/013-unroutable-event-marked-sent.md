# 013 — Un événement non routable était marqué comme envoyé
Date : 2026-11-24 · Phase : 3 · Composant : Worker · Lien : PR `feat/p3-backend-messaging`

## Symptôme
Un événement publié sur `desk.events` sans file liée à sa clé disparaissait en silence, alors que sa ligne d'outbox était marquée `DispatchedAt`.

## Contexte
Avec les confirmations de publication, le broker confirme un message même s'il n'est livré à aucune file (publication non `mandatory`).

## Cause racine
Publication avec `Mandatory=false`. Avec `Mandatory=true`, `RabbitMQ.Client` ne renvoie pas `false` : il lève `PublishReturnException` (312 NO_ROUTE), ce qu'un premier test a révélé par un délai dépassé.

## Solution
Événements publiés avec `Mandatory=true` ; le publisher attrape `PublishReturnException` et renvoie `false` ; la ligne reste en attente et un avertissement « is unroutable » est journalisé une seule fois.

## Pour l'éviter
Tests de `MessagingFailureTests` (événement non routable, échec de publication).
