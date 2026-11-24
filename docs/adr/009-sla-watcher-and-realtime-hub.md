# ADR 009 — SlaWatcher, consommateur temps réel et jeton du hub

Statut : accepté · Date : 2026-11-24

## Contexte
La tranche B ajoute la détection de dépassement de SLA, l'escalade et les notifications temps réel. Trois choix ne sont pas dans le SPEC : comment ne signaler un dépassement qu'une fois, comment parcourir tous les tenants, comment authentifier une connexion WebSocket.

## Décision
- **Émission unique** : table `SlaBreaches`, clé primaire `(TicketId, Kind)`. Le `SlaWatcher` fait `INSERT … ON CONFLICT DO NOTHING` dans la même transaction que la ligne d'outbox : plusieurs instances ou plusieurs cycles ne peuvent pas émettre deux fois. `EscalatedAt` seul ne suffisait pas, car il n'est posé qu'à l'escalade, après l'émission. Limite : un ticket détecté puis sorti de `New`/`Open` avant l'escalade n'est pas ré-émis ; le rejeu DLQ reste possible.
- **Accès multi-tenant** : le seul accès sans filtre de tenant est la requête de candidats du `SlaWatcher` (lecture seule, identifiants). Chaque écriture se fait dans son propre scope, avec `UseTenant`, après rechargement du ticket par le filtre. Un tenant en erreur n'arrête pas les autres.
- **Escalade** : le ticket est sauvegardé avant l'envoi des e-mails ; un conflit `xmin` donne une reprise sans e-mail parasite, une panne SMTP annule la transaction. Un ticket déjà `Urgent` pose `EscalatedAt` et prévient les TenantAdmins sans `TicketEvent`.
- **Consommateur temps réel** : `AgentRealtimeConsumer` est hébergé dans l'API (le hub y vit), derrière `IRealtimeNotifier`. L'API démarre sans broker.
- **Jeton du hub** : les navigateurs ne peuvent pas poser d'en-tête sur un WebSocket ; le JWT est accepté en paramètre `access_token` uniquement pour `/hubs/notifications`. Le risque (jeton dans l'URL, donc dans les journaux d'accès) est limité par la durée de vie de 15 min ; la connexion est fermée à l'expiration du jeton (`CloseOnAuthenticationExpiration`).

## Conséquences
- 15 entités au lieu de 13 (SPEC section 6).
- Le proxy nginx du compose (`docker/nginx.conf`) coupe le journal d'accès du bloc `/hubs/` pour que le jeton ne finisse pas dans le journal d'accès ; tout autre proxy devra faire de même. Risque résiduel accepté : en cas d'échec amont (API arrêtée ou en redémarrage), nginx écrit la ligne `request: "GET /hubs/...?access_token=..."` dans son journal d'erreur, qui va aussi dans `docker logs` ; le jeton expire en 15 min et ces journaux ne sont pas exposés. Côté API, Serilog ne journalise pas la query string.
