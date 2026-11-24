# Journal de troubleshooting

Problèmes réels rencontrés pendant le développement, avec cause et solution (modèle : SPEC section 12).

| N° | Titre | Composant |
| --- | --- | --- |
| [001](001-rabbitmq-erlang-cookie-eacces.md) | RabbitMQ s'arrête au démarrage (`.erlang.cookie: eacces`) | Docker |
| [002](002-tailwind-theme-vars-inlined.md) | Les teintes dérivées du thème ne suivent pas le thème d'entreprise (Tailwind v4) | Web |
| [003](003-migrate-on-startup-breaks-health-tests.md) | Les tests de santé échouent après l'ajout des migrations au démarrage | API |
| [004](004-fielderror-onpush-not-refreshing.md) | Les messages de validation des formulaires ne s'affichent pas (OnPush) | Web |
| [005](005-ef-priority-sort-not-translatable.md) | Le tri des tickets par priorité répond 500 (expression LINQ non traduisible) | API |
| [006](006-postgres-microsecond-precision.md) | Un horodatage renvoyé diffère de celui relu en base (précision) | API |
| [007](007-ef-insert-order-same-table.md) | L'ordre des événements créés dans une même opération n'est pas celui de l'ajout | API |
| [008](008-json-converter-skip-partial.md) | Un identifiant mal formé dans un PATCH répond 500 (`Utf8JsonReader.Skip`) | API |
| [009](009-pagination-int-overflow.md) | `GET /tickets?page=2147483647` répond 500 (débordement d'offset) | API |
| [010](010-replay-dlq-infinite-loop.md) | `replay-dlq` boucle sans fin sur un message qui échoue toujours | Worker |
| [011](011-testcontainers-rabbitmq-management.md) | Testcontainers RabbitMQ : l'API de management est inutilisable par défaut | Tests |
| [012](012-topology-declared-late.md) | Erreur `404 no exchange 'desk.dlx'` juste après le démarrage de l'hôte | Worker |
| [013](013-unroutable-event-marked-sent.md) | Un événement non routable était marqué comme envoyé | Worker |
| [014](014-consumers-dead-after-connection-loss.md) | Les consommateurs restaient morts après une coupure de connexion | Worker |
| [015](015-outbox-invalid-row-blocks-dispatcher.md) | Une ligne d'outbox invalide bloquait le dispatcher | Worker |
