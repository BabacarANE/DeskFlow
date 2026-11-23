# 001 — RabbitMQ ne démarre pas avec un volume nommé (`.erlang.cookie: eacces`)
Date : 2026-11-23 · Phase : 0 · Composant : Docker · Lien : PR de la phase 0 (`feat/phase-0-foundations`)

## Symptôme
`docker compose up` : le conteneur `rabbitmq` sort en erreur (exit 1) et bloque les services qui en dépendent :
`Error when reading /var/lib/rabbitmq/.erlang.cookie: eacces`.

## Contexte
Image `rabbitmq:4-management-alpine`, volume nommé `rabbitmq-data` monté sur `/var/lib/rabbitmq`, Docker Desktop 28.5.2 sur Windows 11. Reproduit sur un volume neuf (`docker compose down -v` puis `up`).

## Cause racine
Dans le volume, `.erlang.cookie` (mode 400) appartenait à `root` (uid 0) alors que RabbitMQ tourne sous l'utilisateur `rabbitmq` (uid 100) : il ne peut pas le lire. Le `chown` du script d'entrée de l'image n'a pas corrigé ce propriétaire au premier démarrage ; la raison exacte n'a pas été établie. Un `chown` manuel du volume suffisait à faire démarrer RabbitMQ.

## Solution
Ne plus monter de volume sur `/var/lib/rabbitmq` dans `docker-compose.yml`. La topologie est redéclarée par l'application au démarrage et les messages à garantir sont dans l'outbox PostgreSQL, donc la perte des files au redémarrage du broker est acceptable pour le développement et la démo.

## Pour l'éviter
Si la persistance du broker devient nécessaire, la rétablir avec un volume dont le propriétaire est corrigé explicitement, et la tester sur un volume neuf (`docker compose down -v`).
