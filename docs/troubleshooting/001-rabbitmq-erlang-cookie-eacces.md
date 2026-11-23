# 001 — RabbitMQ s'arrête au démarrage (`.erlang.cookie: eacces`)
Date : 2026-11-23 · Phase : 0 (corrigé en phase 1) · Composant : Docker · Lien : PR de la phase 0 et PR backend de la phase 1

## Symptôme
`docker compose up` : le conteneur `rabbitmq` sort en erreur (exit 1) et bloque les services qui en dépendent :
`Error when reading /var/lib/rabbitmq/.erlang.cookie: eacces`. Le problème apparaissait par intermittence.

## Contexte
Image `rabbitmq:4-management-alpine`, Docker 28.5.2 sur Windows 11. Healthcheck initial : `rabbitmq-diagnostics -q ping`.

## Cause racine
Le healthcheck s'exécute en `root` dans le conteneur. Quand il tourne avant que le serveur (utilisateur `rabbitmq`, uid 100) ait créé son cookie Erlang, l'outil CLI crée `/var/lib/rabbitmq/.erlang.cookie` appartenant à `root` en mode 400, que le serveur ne peut plus lire. Reproduit avec un simple `docker run` muni de `--health-cmd "rabbitmq-diagnostics -q ping"` (7 erreurs `eacces`), et non reproduit sans healthcheck ni avec le correctif ci-dessous.

## Solution
Exécuter le healthcheck sous l'utilisateur du serveur : `su-exec rabbitmq rabbitmq-diagnostics -q ping` (deux démarrages consécutifs sains, 0 erreur).

## Pour l'éviter
Tout outil `rabbitmq-*` lancé via `docker exec` ou un healthcheck doit l'être en tant que `rabbitmq`. Note : en phase 0, ce problème avait d'abord été attribué à un volume nommé, dont la suppression masquait le défaut sans le corriger (la cause réelle a été établie en phase 1).
