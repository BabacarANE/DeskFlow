# 023 — Un caractère NUL dans une requête provoquait un 500
Date : 2026-11-24 · Phase : 4 · Composant : API · Lien : PR `feat/p4-backend`

## Symptôme
Relevé par l'audit de la phase 4 puis reproduit par un test : `GET /articles?q=%00ab` répondait 500. Un `\u0000` dans un champ texte d'un corps JSON donnait le même résultat.

## Cause racine
PostgreSQL refuse le caractère `0x00` dans une colonne `text` (SQLSTATE 22021) et aucune validation ne l'écartait en amont. Le défaut touchait aussi les tickets, commentaires, catégories, invitations, SLA et thème.

## Solution
Un middleware (`NulCharacterGuard`) placé après l'autorisation vérifie la query string et toutes les chaînes du corps JSON (y compris la forme échappée `\u0000` et les noms de propriété) et répond 422 en ProblemDetails avec les champs en cause ; le contenu n'est pas journalisé. Effet de bord : un corps JSON de plus de 1 Mo reçoit 413.

## Pour l'éviter
Test `A_NUL_character_in_any_text_input_is_a_422_…` sur une famille de routes ; toute nouvelle route passe par le middleware.

## Problèmes trouvés en corrigeant (réaudit)
- Le garde lui-même répondait 500 sur de l'UTF-8 invalide : `Utf8JsonReader.GetString()` lève `InvalidOperationException`, et seule `JsonException` était attrapée.
- Indépendamment du garde, le binder du framework répondait déjà 500 sur un corps en UTF-8 invalide et sur un JSON de 200 niveaux : le garde répond maintenant 400.
- Le 413 ne fonctionnait pas : le dépassement du tampon lève `IOException`, pas `InvalidDataException` ; les deux sont attrapées et `Content-Length` est contrôlé avant. Le chemin « corps chunked sans `Content-Length` » n'est pas testé.
- Hors périmètre du garde : en-têtes, cookies et paramètres de chemin (Kestrel rejette les en-têtes invalides, les identifiants de chemin sont des Guid).
