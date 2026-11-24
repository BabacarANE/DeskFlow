# ADR 008 — Conception de la couche de messagerie

Statut : accepté · Date : 2026-11-23

## Contexte
Le SPEC section 8 exige outbox, idempotence, retries, dead-letter queue et rejeu, avec le client officiel `RabbitMQ.Client` (ADR 003). Il laisse ouverts la structure des files de délai, le sens de « 3 tentatives » et le contenu de l'outbox.

## Décision
- **Files de délai** : une file TTL par consommateur et par palier, `desk.<consommateur>.retry.<5s|30s|2m>`. RabbitMQ n'expire un message que lorsqu'il est en tête de file : un TTL par message sur une seule file bloquerait les messages à délai court derrière un message à délai long. Le délai figure dans le nom de la file, donc le changer crée une nouvelle file sans erreur `PRECONDITION_FAILED`. Chemin : publication sur `desk.retry` (clé `<consommateur>.retry.<délai>`), puis dead-lettering vers `desk.retry` (clé `<consommateur>`), lié à `desk.<consommateur>`.
- **Tentatives** : une livraison initiale plus 3 reprises (5 s, 30 s, 2 min) : 4 livraisons, puis `desk.dlq` avec `attempt` = 3. Les délais se règlent par `Messaging:RetryDelays` ; les tests utilisent des délais courts.
- **Message empoisonné** : JSON ou enveloppe invalide, tenant manquant, nul ou différent de l'en-tête `tenant-id`, entité introuvable dans le tenant du message : directement en `desk.dlq`, sans reprise.
- **En-têtes d'échec** posés en DLQ : `x-failed-consumer`, `x-failure-reason`, `x-failure-kind` (`poison` ou `retries-exhausted`), `x-failed-at`, `x-original-exchange`, `x-original-routing-key`. `replay-dlq` ajoute `x-replay-count` et remet `attempt` à 0.
- **Idempotence** : `INSERT … ON CONFLICT DO NOTHING` sur `ProcessedMessages` dans la transaction du handler ; un doublon concurrent attend la première transaction.
- **Outbox** : l'enveloppe complète est stockée dans `Payload` (jsonb) ; l'identifiant de la ligne est le `messageId`. Un lot = une transaction, `FOR UPDATE SKIP LOCKED`, publication avec confirmations, puis `DispatchedAt`. Garantie : au moins une fois.
- **Topologie complète dès la tranche A** : les six files de `docs/api/events.md` sont déclarées par le Worker et l'API même quand leur consommateur n'est pas encore implémenté, afin qu'aucun événement ne soit perdu entre deux livraisons ; seules les classes `[Consumer]` enregistrées sont abonnées.
- **Rejeu** : `replay-dlq` republie sur `desk.events` avec la clé d'origine : tous les consommateurs liés à cette clé reçoivent le message, et l'idempotence fait ignorer le doublon à ceux qui l'ont déjà traité. Le nombre de messages lus est fixé à la profondeur de la DLQ au départ, pour ne pas reboucler sur un message qui y retourne.

## Conséquences
- Ajouter un consommateur = une classe `[Consumer("suffixe", Hôte, "routing.key")]` ; la topologie suit le contrat.
- Les retries d'un message prennent 2 min 35 s avec les délais par défaut.
- Un message rejoué vers un consommateur qui a déjà réussi est ignoré, pas retraité.

## Compléments (audit de la tranche A)
- **Fiabilité à la panne** : événements publiés en `mandatory` (non routable = échec, la ligne reste en attente) ; consommateurs supervisés avec réabonnement (1, 2, 5 s) ; arrêt propre borné par `Messaging:ShutdownTimeout` ; échec de publication retry/DLQ = nack avec remise en file ; ligne d'outbox invalide marquée en échec (`FailedAt`, `FailureReason`). Limite connue : 50 lignes non routables ou plus en tête de l'outbox bloqueraient encore un lot.
- **Couplage compose** : le service `worker` attend `api` en bonne santé, car l'API applique les migrations ; sans schéma, un consommateur échouerait en reprises puis en DLQ.
